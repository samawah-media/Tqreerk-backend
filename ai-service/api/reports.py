"""Report AI endpoints — ingest, summarize, translate (single + bulk)."""
import asyncio
import json
import logging
from uuid import UUID, uuid4

from fastapi import APIRouter, Depends, HTTPException
from psycopg import AsyncConnection

from core.db import conn_ctx, get_conn
from models.ingest import (
    BulkIngestItem,
    BulkIngestRequest,
    BulkJobsResponse,
    BulkTranslateItem,
    BulkTranslateRequest,
    CreatedJob,
    IngestRequest,
    IngestResponse,
    JobStatusResponse,
    SummarizeRequest,
    SummarizeResponse,
    TranslateRequest,
    TranslateResponse,
)
from pipelines.ingest import ingest_report
from services.gemini import summarize_report
from services.translate import detect_language, translate_pdf

logger = logging.getLogger(__name__)

# Cap concurrent bulk operations to keep Gemini quota and DB load reasonable.
_BULK_CONCURRENCY = 3

router = APIRouter(prefix="/reports", tags=["reports"])


@router.post("/ingest", response_model=IngestResponse, status_code=202)
async def ingest(body: IngestRequest):
    """Trigger PDF ingestion: extract text via Gemini and store embeddings."""
    pages = await ingest_report(body.report_id, body.file_url)
    return IngestResponse(report_id=body.report_id, pages_processed=pages)


@router.post("/summarize", response_model=SummarizeResponse)
async def summarize(
    body: SummarizeRequest,
    conn: AsyncConnection = Depends(get_conn),
):
    """Generate executive summary and key findings from stored page content."""
    cur = await conn.execute(
        """
        SELECT "Content"
        FROM report_pages
        WHERE "ReportId" = %s
        ORDER BY "PageNumber"
        """,
        [str(body.report_id)],
    )
    rows = await cur.fetchall()
    if not rows:
        raise HTTPException(status_code=404, detail="Report pages not found — ingest first")

    pages_content = [r[0] for r in rows]
    result = summarize_report(pages_content)
    return SummarizeResponse(
        report_id=body.report_id,
        summary=result.summary,
        key_findings=result.key_findings,
        topics=result.topics,
    )


@router.post("/translate", response_model=TranslateResponse)
async def translate(
    body: TranslateRequest,
    conn: AsyncConnection = Depends(get_conn),
):
    """Translate the report PDF using Google Cloud Translation API v3 Document Translation.

    Auto-detects source language from stored page content (ingest first).
    Arabic → English, anything else → Arabic.
    Returns the GCS URI of the translated PDF; store it in ReportTranslation.
    """
    # Use stored page text for language detection — no extra API round-trip needed
    cur = await conn.execute(
        'SELECT "Content" FROM report_pages WHERE "ReportId" = %s ORDER BY "PageNumber" LIMIT 1',
        [str(body.report_id)],
    )
    row = await cur.fetchone()
    if not row:
        raise HTTPException(status_code=404, detail="Report pages not found — ingest first")

    source_language = detect_language(row[0])
    target_language = "en" if source_language == "ar" else "ar"

    # Use the output_prefix exactly as .NET sent it — no language subfolder appended.
    output_prefix = body.output_prefix
    if not output_prefix.startswith("gs://"):
        raise HTTPException(status_code=400, detail="output_prefix must start with gs://")
    if not output_prefix.endswith("/"):
        output_prefix += "/"

    translated_url = translate_pdf(
        gcs_input_uri=body.file_url,
        output_prefix=output_prefix,
        source_language=source_language,
        target_language=target_language,
    )
    return TranslateResponse(
        report_id=body.report_id,
        source_language=source_language,
        target_language=target_language,
        translated_file_url=translated_url,
    )


# ── Bulk endpoints (async, fire-and-forget via ai_jobs table) ────────────────
#
# Pattern:
#   1. Client POSTs items → we INSERT one ai_jobs row per item (status=Pending)
#   2. We spawn a single asyncio task that processes all items with bounded
#      concurrency, updating each ai_jobs row as it progresses
#   3. We return 202 with the list of job_ids — client polls GET /jobs/{id}
#
# Why: 50+ items × ~30 s each easily exceeds Cloud Run's 5 min request timeout.
# This pattern keeps the HTTP request fast (just inserts) and runs the heavy
# work in the background.
#
# Caveat: if the Cloud Run instance is recycled mid-batch (rare with
# min-instances=1), in-flight tasks die and stay "Processing". A scheduled job
# can mark stale jobs as Failed — TODO when we add scheduled cleanup.

# ── DB helpers ───────────────────────────────────────────────────────────────

async def _insert_job(
    conn: AsyncConnection,
    job_id: UUID,
    job_type: str,
    report_id: UUID,
    user_id: UUID | None,
    organization_id: UUID | None,
    input_data: dict,
) -> None:
    await conn.execute(
        """
        INSERT INTO ai_jobs ("Id", "UserId", "OrganizationId", "ReportId",
                             "JobType", "Status", "TokensUsed",
                             "InputData", "CreatedAt")
        VALUES (%s, %s, %s, %s, %s, 'Pending', 0, %s, now())
        """,
        [
            str(job_id),
            str(user_id) if user_id else None,
            str(organization_id) if organization_id else None,
            str(report_id),
            job_type,
            json.dumps(input_data),
        ],
    )


async def _mark_processing(job_id: UUID) -> None:
    async with conn_ctx() as conn:
        await conn.execute(
            'UPDATE ai_jobs SET "Status"=\'Processing\', "StartedAt"=now() WHERE "Id"=%s',
            [str(job_id)],
        )
        await conn.commit()


async def _mark_completed(job_id: UUID, output: dict) -> None:
    async with conn_ctx() as conn:
        await conn.execute(
            """
            UPDATE ai_jobs
            SET "Status"='Completed', "OutputData"=%s, "CompletedAt"=now()
            WHERE "Id"=%s
            """,
            [json.dumps(output), str(job_id)],
        )
        await conn.commit()


async def _mark_failed(job_id: UUID, error: str) -> None:
    async with conn_ctx() as conn:
        await conn.execute(
            """
            UPDATE ai_jobs
            SET "Status"='Failed', "ErrorMessage"=%s, "CompletedAt"=now()
            WHERE "Id"=%s
            """,
            [error[:1000], str(job_id)],
        )
        await conn.commit()


# ── Worker functions (run in background) ─────────────────────────────────────

async def _do_ingest_summarize_job(
    job_id: UUID, report_id: UUID, file_url: str
) -> None:
    await _mark_processing(job_id)
    try:
        pages_processed = await ingest_report(report_id, file_url)

        async with conn_ctx() as conn:
            cur = await conn.execute(
                'SELECT "Content" FROM report_pages '
                'WHERE "ReportId" = %s ORDER BY "PageNumber"',
                [str(report_id)],
            )
            rows = await cur.fetchall()

        if not rows:
            raise RuntimeError("Ingest produced no pages")

        summary = summarize_report([r[0] for r in rows])
        await _mark_completed(job_id, {
            "pages_processed": pages_processed,
            "summary": summary.summary,
            "key_findings": summary.key_findings,
            "topics": summary.topics,
        })
    except Exception as exc:
        logger.exception("ingest+summarize job %s failed", job_id)
        await _mark_failed(job_id, str(exc))


async def _do_translate_job(
    job_id: UUID, report_id: UUID, file_url: str, output_prefix: str
) -> None:
    await _mark_processing(job_id)
    try:
        # Validate prefix shape early
        if not output_prefix.startswith("gs://"):
            raise ValueError("output_prefix must start with gs://")
        prefix = output_prefix if output_prefix.endswith("/") else output_prefix + "/"

        # Need at least one ingested page for language detection
        async with conn_ctx() as conn:
            cur = await conn.execute(
                'SELECT "Content" FROM report_pages '
                'WHERE "ReportId" = %s ORDER BY "PageNumber" LIMIT 1',
                [str(report_id)],
            )
            row = await cur.fetchone()

        if not row:
            raise RuntimeError("Report pages not found — ingest first")

        source_language = detect_language(row[0])
        target_language = "en" if source_language == "ar" else "ar"

        translated_url = translate_pdf(
            gcs_input_uri=file_url,
            output_prefix=prefix,
            source_language=source_language,
            target_language=target_language,
        )
        await _mark_completed(job_id, {
            "source_language": source_language,
            "target_language": target_language,
            "translated_file_url": translated_url,
        })
    except Exception as exc:
        logger.exception("translate job %s failed", job_id)
        await _mark_failed(job_id, str(exc))


async def _run_batch(coros: list) -> None:
    """Run a list of coroutines with bounded concurrency."""
    sem = asyncio.Semaphore(_BULK_CONCURRENCY)

    async def gated(coro):
        async with sem:
            return await coro

    await asyncio.gather(*(gated(c) for c in coros), return_exceptions=True)


# ── Endpoints ────────────────────────────────────────────────────────────────

@router.post(
    "/bulk/ingest-summarize",
    response_model=BulkJobsResponse,
    status_code=202,
)
async def bulk_ingest_summarize(
    body: BulkIngestRequest,
    conn: AsyncConnection = Depends(get_conn),
):
    """Queue ingest+summarize for many reports. Returns immediately with job IDs.

    Poll `GET /api/ai/reports/jobs/{job_id}` to check progress on each.
    """
    created: list[CreatedJob] = []
    for item in body.items:
        job_id = uuid4()
        await _insert_job(
            conn,
            job_id=job_id,
            job_type="Ingest",
            report_id=item.report_id,
            user_id=body.user_id,
            organization_id=body.organization_id,
            input_data={"file_url": item.file_url, "step": "ingest+summarize"},
        )
        created.append(CreatedJob(job_id=job_id, report_id=item.report_id))
    await conn.commit()

    # Spawn the worker in background — request returns immediately
    coros = [
        _do_ingest_summarize_job(j.job_id, item.report_id, item.file_url)
        for j, item in zip(created, body.items)
    ]
    asyncio.create_task(_run_batch(coros))

    return BulkJobsResponse(jobs=created)


@router.post(
    "/bulk/translate",
    response_model=BulkJobsResponse,
    status_code=202,
)
async def bulk_translate(
    body: BulkTranslateRequest,
    conn: AsyncConnection = Depends(get_conn),
):
    """Queue translation for many reports. Returns immediately with job IDs."""
    created: list[CreatedJob] = []
    for item in body.items:
        job_id = uuid4()
        await _insert_job(
            conn,
            job_id=job_id,
            job_type="Translate",
            report_id=item.report_id,
            user_id=body.user_id,
            organization_id=body.organization_id,
            input_data={"file_url": item.file_url, "output_prefix": item.output_prefix},
        )
        created.append(CreatedJob(job_id=job_id, report_id=item.report_id))
    await conn.commit()

    coros = [
        _do_translate_job(j.job_id, item.report_id, item.file_url, item.output_prefix)
        for j, item in zip(created, body.items)
    ]
    asyncio.create_task(_run_batch(coros))

    return BulkJobsResponse(jobs=created)


@router.get("/jobs/{job_id}", response_model=JobStatusResponse)
async def get_job(
    job_id: UUID,
    conn: AsyncConnection = Depends(get_conn),
):
    """Poll the status of a previously-queued bulk job."""
    cur = await conn.execute(
        """
        SELECT "Id", "ReportId", "JobType", "Status",
               "ErrorMessage", "OutputData", "StartedAt", "CompletedAt"
        FROM ai_jobs
        WHERE "Id" = %s
        """,
        [str(job_id)],
    )
    row = await cur.fetchone()
    if not row:
        raise HTTPException(status_code=404, detail="Job not found")

    output = row[5]
    if isinstance(output, str):
        try:
            output = json.loads(output)
        except json.JSONDecodeError:
            output = None

    return JobStatusResponse(
        job_id=row[0],
        report_id=row[1],
        job_type=row[2],
        status=row[3],
        error_message=row[4],
        output_data=output,
        started_at=row[6].isoformat() if row[6] else None,
        completed_at=row[7].isoformat() if row[7] else None,
    )
