"""Report AI endpoints — ingest, summarize, translate (single + bulk)."""
import asyncio
import json
import logging
from uuid import UUID, uuid4

from fastapi import APIRouter, Depends, HTTPException
from psycopg import AsyncConnection

import numpy as np

from core.db import conn_ctx, get_conn
from models.ingest import (
    BulkIngestItem,
    BulkIngestRequest,
    BulkJobsResponse,
    BulkTranslateItem,
    BulkTranslateRequest,
    CompareRequest,
    CompareResponse,
    CreatedJob,
    Indicator,
    IngestRequest,
    IngestResponse,
    InsightsRequest,
    InsightsResponse,
    JobStatusResponse,
    ReportPageContent,
    ReportPagesResponse,
    ReportSimilarity,
    SharedIndicator,
    SummarizeRequest,
    SummarizeResponse,
    Trend,
    TranslateRequest,
    TranslateResponse,
)
from pipelines.ingest import ingest_report
from services.gemini import compare_reports, extract_insights, summarize_report
from services.translate import detect_language, translate_pdf

logger = logging.getLogger(__name__)

# Cap concurrent bulk operations to keep Gemini quota and DB load reasonable.
_BULK_CONCURRENCY = 3

router = APIRouter(prefix="/reports", tags=["reports"])


async def _do_ingest_only_job(job_id: UUID, report_id: UUID, file_url: str) -> None:
    """Worker: ingest PDF only (no summarize). Updates ai_jobs as it progresses."""
    await _mark_processing(job_id)
    try:
        pages_processed = await ingest_report(report_id, file_url)
        await _mark_completed(job_id, {"pages_processed": pages_processed})
    except Exception as exc:
        logger.exception("ingest job %s failed", job_id)
        await _mark_failed(job_id, str(exc))


async def _ensure_ingested(
    report_id: UUID, conn: AsyncConnection
) -> dict | None:
    """Make sure a report has page content; if not, kick off an auto-ingest.

    Returns:
        None  → report is ingested, caller can proceed
        dict  → not ingested; dict has {report_id, job_id, status, message}
                — caller should raise HTTPException(202, detail=dict)

    Behaviour:
        1. If `report_pages` already has rows → ingested, return None.
        2. If an Ingestion job is already Pending/Processing → return its info,
           don't kick off a duplicate.
        3. Otherwise look up the report's `FileUrl` and start a new ingest.
    """
    rid = str(report_id)

    # 1. Already ingested?
    cur = await conn.execute(
        'SELECT 1 FROM report_pages WHERE "ReportId" = %s LIMIT 1',
        [rid],
    )
    if await cur.fetchone():
        return None

    # 2. Ingest already in flight for this report?
    cur = await conn.execute(
        '''
        SELECT "Id", "Status" FROM ai_jobs
        WHERE "ReportId" = %s
          AND "JobType" = 'Ingestion'
          AND "Status" IN ('Pending', 'Processing')
        ORDER BY "CreatedAt" DESC LIMIT 1
        ''',
        [rid],
    )
    in_flight = await cur.fetchone()
    if in_flight:
        return {
            "report_id": rid,
            "job_id": str(in_flight[0]),
            "status": in_flight[1],
            "message": "Ingest already in progress — retry when status is Completed",
        }

    # 3. Look up the report's FileUrl from the .NET-managed reports table.
    cur = await conn.execute(
        'SELECT "FileUrl" FROM reports WHERE "Id" = %s',
        [rid],
    )
    row = await cur.fetchone()
    if not row or not row[0]:
        raise HTTPException(
            status_code=404,
            detail=f"Report {rid} not found or has no file_url — cannot auto-ingest",
        )
    file_url = row[0]

    # 4. Create the job and spawn the worker.
    job_id = uuid4()
    await _insert_job(
        conn,
        job_id=job_id,
        job_type="Ingestion",
        report_id=report_id,
        input_data={"file_url": file_url, "step": "auto-ingest"},
    )
    await conn.commit()
    asyncio.create_task(_do_ingest_only_job(job_id, report_id, file_url))

    logger.info("auto-ingest triggered for report=%s job=%s", rid, job_id)
    return {
        "report_id": rid,
        "job_id": str(job_id),
        "status": "Pending",
        "message": "Ingest started — retry when status is Completed",
    }


@router.post("/ingest", response_model=IngestResponse, status_code=202)
async def ingest(
    body: IngestRequest,
    conn: AsyncConnection = Depends(get_conn),
):
    """Queue PDF ingestion (fire-and-forget). Returns immediately with a job_id.

    Ingest is too slow for sync HTTP — a 50-page Arabic PDF takes 3-10 min
    because each page is a Gemini Vision call. The .NET HttpClient default
    timeout is 100 s and Cloud Run caps at 3600 s, so we don't try to wait.

    Poll `GET /api/ai/reports/jobs/{job_id}` for status. When `Completed`,
    `output_data.pages_processed` will hold the page count.
    """
    job_id = uuid4()
    await _insert_job(
        conn,
        job_id=job_id,
        job_type="Ingestion",
        report_id=body.report_id,
        input_data={"file_url": body.file_url, "step": "ingest"},
    )
    await conn.commit()

    # Spawn worker — request returns immediately with the job_id
    asyncio.create_task(_do_ingest_only_job(job_id, body.report_id, body.file_url))

    return IngestResponse(report_id=body.report_id, job_id=job_id)


@router.post("/summarize", response_model=SummarizeResponse)
async def summarize(
    body: SummarizeRequest,
    conn: AsyncConnection = Depends(get_conn),
):
    """Generate executive summary and key findings from stored page content."""
    not_ready = await _ensure_ingested(body.report_id, conn)
    if not_ready:
        raise HTTPException(status_code=202, detail=not_ready)

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
    pages_content = [r[0] for r in rows]
    result = summarize_report(pages_content)
    return SummarizeResponse(
        report_id=body.report_id,
        summary=result.summary,
        key_findings=result.key_findings,
        topics=result.topics,
    )


@router.get("/{report_id}/pages", response_model=ReportPagesResponse)
async def get_report_pages(
    report_id: UUID,
    conn: AsyncConnection = Depends(get_conn),
):
    """Return all per-page text extracted by Gemini Vision during ingest.

    Useful for debugging ingest quality, displaying extracted content in admin
    UIs, or feeding raw page text to other downstream processing.

    Auto-triggers ingest if the report hasn't been ingested yet (returns 202
    with the new job_id; retry once it Completes).
    """
    not_ready = await _ensure_ingested(report_id, conn)
    if not_ready:
        raise HTTPException(status_code=202, detail=not_ready)

    cur = await conn.execute(
        'SELECT "PageNumber", "Content" FROM report_pages '
        'WHERE "ReportId" = %s ORDER BY "PageNumber"',
        [str(report_id)],
    )
    rows = await cur.fetchall()

    return ReportPagesResponse(
        report_id=report_id,
        page_count=len(rows),
        pages=[ReportPageContent(page_number=r[0], content=r[1]) for r in rows],
    )


@router.post("/insights", response_model=InsightsResponse)
async def insights(
    body: InsightsRequest,
    conn: AsyncConnection = Depends(get_conn),
):
    """Extract structured indicators and trends from an already-ingested report.

    Reads stored `report_pages.Content` and asks Gemini for structured output.
    Result can be persisted by .NET into `report_ai_content.Indicators` /
    `report_ai_content.Trends` (jsonb columns).
    """
    not_ready = await _ensure_ingested(body.report_id, conn)
    if not_ready:
        raise HTTPException(status_code=202, detail=not_ready)

    cur = await conn.execute(
        'SELECT "Content" FROM report_pages '
        'WHERE "ReportId" = %s ORDER BY "PageNumber"',
        [str(body.report_id)],
    )
    rows = await cur.fetchall()

    result = extract_insights([r[0] for r in rows])
    return InsightsResponse(
        report_id=body.report_id,
        indicators=[Indicator(**i) for i in result.get("indicators", [])],
        trends=[Trend(**t) for t in result.get("trends", [])],
    )


def _cosine(a: np.ndarray, b: np.ndarray) -> float:
    na, nb = float(np.linalg.norm(a)), float(np.linalg.norm(b))
    if na == 0.0 or nb == 0.0:
        return 0.0
    return float(np.dot(a, b) / (na * nb))


@router.post("/compare", response_model=CompareResponse)
async def compare(
    body: CompareRequest,
    conn: AsyncConnection = Depends(get_conn),
):
    """Compare 2+ reports.

    Two layers:
      1. Numerical similarity — pairwise cosine on each report's mean page
         embedding. Cheap, runs in pgvector + numpy, gives a 0..1 score per pair.
      2. Qualitative comparison — sends the stored summaries + key findings of
         each report to Gemini for structured output (common topics, key
         differences, shared indicators).

    Each report must already be ingested AND summarized.
    """
    if len(body.report_ids) < 2:
        raise HTTPException(status_code=400, detail="Need at least 2 report_ids to compare")

    # Auto-ingest any reports that haven't been ingested yet. Returns 202 with
    # the list of started/in-flight jobs — caller retries when all Completed.
    pending: list[dict] = []
    for rid in body.report_ids:
        not_ready = await _ensure_ingested(rid, conn)
        if not_ready:
            pending.append(not_ready)
    if pending:
        raise HTTPException(
            status_code=202,
            detail={
                "message": "One or more reports are not yet ingested — retry when all jobs Complete",
                "pending_jobs": pending,
            },
        )

    ids = [str(rid) for rid in body.report_ids]

    # 1. Mean page embedding per report — done in numpy, since pgvector's avg()
    #    aggregate isn't universally available.
    emb_cur = await conn.execute(
        'SELECT "ReportId", embedding FROM report_pages '
        'WHERE "ReportId" = ANY(%s) AND embedding IS NOT NULL',
        [ids],
    )
    emb_rows = await emb_cur.fetchall()

    by_report: dict[str, list[np.ndarray]] = {}
    for rid, emb in emb_rows:
        by_report.setdefault(str(rid), []).append(np.asarray(emb, dtype=np.float32))

    means: dict[str, np.ndarray] = {
        rid: np.mean(np.stack(vecs), axis=0) for rid, vecs in by_report.items()
    }

    similarities: list[ReportSimilarity] = []
    for i in range(len(ids)):
        for j in range(i + 1, len(ids)):
            ra, rb = ids[i], ids[j]
            if ra in means and rb in means:
                similarities.append(ReportSimilarity(
                    report_a=ra, report_b=rb,
                    score=round(_cosine(means[ra], means[rb]), 4),
                ))

    # 2. Pull summary + key_findings from report_ai_content for each report.
    ai_cur = await conn.execute(
        '''
        SELECT "ReportId", "Summary", "KeyFindings"
        FROM report_ai_content
        WHERE "ReportId" = ANY(%s)
        ''',
        [ids],
    )
    ai_rows = await ai_cur.fetchall()
    by_id_ai: dict[str, tuple] = {str(r[0]): (r[1], r[2]) for r in ai_rows}

    reports_for_gemini: list[dict] = []
    for rid in ids:
        summary, key_findings_raw = by_id_ai.get(rid, (None, None))
        # KeyFindings is jsonb — psycopg3 returns it parsed already
        if isinstance(key_findings_raw, str):
            try:
                key_findings = json.loads(key_findings_raw)
            except json.JSONDecodeError:
                key_findings = []
        else:
            key_findings = key_findings_raw or []
        reports_for_gemini.append({
            "report_id": rid,
            "summary": summary,
            "key_findings": key_findings,
        })

    # If none of the reports have summaries, the comparison won't be useful
    if not any(r.get("summary") for r in reports_for_gemini):
        raise HTTPException(
            status_code=400,
            detail="No report has a stored summary — run /summarize on each first",
        )

    qualitative = compare_reports(reports_for_gemini)

    return CompareResponse(
        report_ids=body.report_ids,
        common_topics=qualitative.get("common_topics", []),
        key_differences=qualitative.get("key_differences", []),
        shared_indicators=[
            SharedIndicator(**si) for si in qualitative.get("shared_indicators", [])
        ],
        overall_summary=qualitative.get("overall_summary", ""),
        similarities=similarities,
    )


@router.post("/translate", response_model=TranslateResponse)
async def translate(
    body: TranslateRequest,
    conn: AsyncConnection = Depends(get_conn),
):
    """Translate the report PDF using Google Cloud Translation API v3 Document Translation.

    Auto-detects source language from stored page content (auto-ingests if missing).
    Arabic → English, anything else → Arabic.
    Returns the GCS URI of the translated PDF; store it in ReportTranslation.
    """
    not_ready = await _ensure_ingested(body.report_id, conn)
    if not_ready:
        raise HTTPException(status_code=202, detail=not_ready)

    # Use stored page text for language detection — no extra API round-trip needed
    cur = await conn.execute(
        'SELECT "Content" FROM report_pages WHERE "ReportId" = %s ORDER BY "PageNumber" LIMIT 1',
        [str(body.report_id)],
    )
    row = await cur.fetchone()

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
    input_data: dict,
) -> None:
    await conn.execute(
        """
        INSERT INTO ai_jobs ("Id", "ReportId", "JobType", "Status",
                             "TokensUsed", "InputData", "CreatedAt")
        VALUES (%s, %s, %s, 'Pending', 0, %s, now())
        """,
        [
            str(job_id),
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
            job_type="Ingestion",
            report_id=item.report_id,
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
            job_type="Translation",
            report_id=item.report_id,
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
