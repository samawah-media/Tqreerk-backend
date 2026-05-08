"""Report AI endpoints — ingest, summarize, translate (single + bulk).

Storage migrated from `report_pages` to `report_chunks`. Each PDF page is now
stored as one or more sub-page chunks (~500 tokens each) with metadata
(section_title, page_type, language). Page-level features (summarize, insights,
compare, "give me page N") aggregate chunks back to page granularity.

Background work model
=====================
This module never spawns background tasks. Endpoints only INSERT into ai_jobs
(status='Pending') and return immediately; a separate Cloud Run service
(WORKER_MODE=worker) claims and runs each row via FOR UPDATE SKIP LOCKED. See
pipelines/jobs.py for the runner / claim logic.
"""
import asyncio
import json
import logging
import time
from uuid import UUID, uuid4

from fastapi import APIRouter, Depends, HTTPException
from psycopg import AsyncConnection

import numpy as np

from core.config import settings
from core.db import get_conn
from services import doc_extractor
from models.ingest import (
    BulkIngestRequest,
    BulkJobsResponse,
    BulkSummarizeRequest,
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
from pipelines.jobs import insert_job
from services.gemini import compare_reports, extract_insights, summarize_report
from services.quota import assert_under_job_quota
from services.translate import detect_source_language, translate_pdf

logger = logging.getLogger(__name__)

router = APIRouter(prefix="/reports", tags=["reports"])


# ── Helpers ──────────────────────────────────────────────────────────────────

async def _fetch_pages_aggregated(
    conn: AsyncConnection, report_id: UUID,
) -> list[tuple[int, str]]:
    """Aggregate report_chunks back to page-level (page_number, content).

    Used by features that operate on pages (summarize, insights, page lookups).
    Chunks are concatenated in (PageNumber, ChunkIndex) order with blank lines
    between them.
    """
    cur = await conn.execute(
        """
        SELECT "PageNumber",
               string_agg("Content", E'\n\n' ORDER BY "ChunkIndex") AS content
        FROM report_chunks
        WHERE "ReportId" = %s
        GROUP BY "PageNumber"
        ORDER BY "PageNumber"
        """,
        [str(report_id)],
    )
    rows = await cur.fetchall()
    return [(r[0], r[1]) for r in rows]


async def _ensure_ingested(
    report_id: UUID, conn: AsyncConnection,
) -> dict | None:
    """Make sure a report has chunk content; if not, queue an auto-ingest job.

    Returns:
        None  → report is ingested, caller can proceed
        dict  → not ingested; dict has {report_id, job_id, status, message}
                — caller should raise HTTPException(202, detail=dict)

    Behaviour:
        1. If `report_chunks` already has rows → ingested, return None.
        2. If an Ingestion job is already Pending/Processing → return its info,
           don't queue a duplicate.
        3. Otherwise look up the report's `FileUrl` and queue a new ingest
           in ai_jobs. The worker service picks it up and runs it.
    """
    rid = str(report_id)

    # 1. Already ingested?
    cur = await conn.execute(
        'SELECT 1 FROM report_chunks WHERE "ReportId" = %s LIMIT 1',
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

    # 4. Queue the job. Worker service will claim it on its next poll.
    job_id = uuid4()
    await insert_job(
        conn,
        job_id=job_id,
        job_type="Ingestion",
        report_id=report_id,
        input_data={"file_url": file_url, "step": "auto-ingest"},
    )
    await conn.commit()

    logger.info("auto-ingest queued for report=%s job=%s", rid, job_id)
    return {
        "report_id": rid,
        "job_id": str(job_id),
        "status": "Pending",
        "message": "Ingest queued — retry when status is Completed",
    }


# ── Endpoints ────────────────────────────────────────────────────────────────

@router.post("/ingest", response_model=IngestResponse, status_code=202)
async def ingest(
    body: IngestRequest,
    conn: AsyncConnection = Depends(get_conn),
):
    """Queue PDF ingestion. Returns immediately with a job_id.

    The job is picked up by the worker service (WORKER_MODE=worker) on its
    next poll cycle (~3 s). Poll `GET /api/ai/reports/jobs/{job_id}` for
    status. When `Completed`, `output_data.pages_processed` and
    `output_data.chunks_inserted` will hold the counts.
    """
    job_id = uuid4()
    logger.info(
        "[ingest] enqueue job=%s report=%s file=%s extractor=%s",
        job_id, body.report_id, body.file_url, body.extractor,
    )
    await insert_job(
        conn,
        job_id=job_id,
        job_type="Ingestion",
        report_id=body.report_id,
        input_data={
            "file_url": body.file_url,
            "step": "ingest",
            "extractor": body.extractor,
        },
    )
    await conn.commit()

    # Fire-and-forget: GPU service claims the job, runs the pipeline, and
    # writes Completed/Failed directly into ai_jobs. No worker involved.
    asyncio.create_task(
        doc_extractor.trigger_ingest(str(job_id), str(body.report_id), body.file_url),
    )

    logger.info("[ingest] job=%s queued (Pending), GPU trigger fired", job_id)
    return IngestResponse(report_id=body.report_id, job_id=job_id)


@router.post("/summarize", response_model=SummarizeResponse)
async def summarize(
    body: SummarizeRequest,
    conn: AsyncConnection = Depends(get_conn),
):
    """Generate executive summary and key findings from stored chunk content."""
    not_ready = await _ensure_ingested(body.report_id, conn)
    if not_ready:
        raise HTTPException(status_code=202, detail=not_ready)

    pages = await _fetch_pages_aggregated(conn, body.report_id)
    pages_content = [content for _, content in pages]

    # Pin the output language to the report's OriginalLanguage. See the
    # matching block in pipelines/jobs.py:run_summarize_job — same reason
    # (Gemini's language auto-detect is unreliable on mixed-language PDFs).
    lang_cur = await conn.execute(
        'SELECT "OriginalLanguage" FROM reports WHERE "Id" = %s',
        [str(body.report_id)],
    )
    lang_row = await lang_cur.fetchone()
    language = (lang_row[0] if lang_row and lang_row[0] else "ar").lower()

    # Wrap in to_thread because summarize_report is sync and its retry
    # path can sleep for minutes on quota errors. Keeping it on the event
    # loop would freeze every other request on this pod (chat sessions,
    # /docs, health, etc. all 504 at Cloud Run's 300 s LB timeout).
    result = await asyncio.to_thread(
        summarize_report, pages_content, language=language,
    )
    return SummarizeResponse(
        report_id=body.report_id,
        summary=result.summary,
        key_findings=result.key_findings,
        topics=result.topics,
        indicators=[i.model_dump(mode="json") for i in result.indicators],
        trends=[t.model_dump(mode="json") for t in result.trends],
    )


@router.get("/{report_id}/pages", response_model=ReportPagesResponse)
async def get_report_pages(
    report_id: UUID,
    conn: AsyncConnection = Depends(get_conn),
):
    """Return all per-page text extracted by Gemini Vision during ingest.

    Pages are reconstructed by concatenating their chunks in chunk_index order
    so the response shape stays stable for callers that pre-date chunking.

    Auto-queues an ingest job if the report hasn't been ingested yet (returns
    202 with the new job_id; retry once it Completes).
    """
    not_ready = await _ensure_ingested(report_id, conn)
    if not_ready:
        raise HTTPException(status_code=202, detail=not_ready)

    pages = await _fetch_pages_aggregated(conn, report_id)

    return ReportPagesResponse(
        report_id=report_id,
        page_count=len(pages),
        pages=[ReportPageContent(page_number=p, content=c) for p, c in pages],
    )


@router.post("/insights", response_model=InsightsResponse)
async def insights(
    body: InsightsRequest,
    conn: AsyncConnection = Depends(get_conn),
):
    """Extract structured indicators and trends from an already-ingested report.

    Reads stored chunks (aggregated to pages) and asks Gemini for structured
    output. Result can be persisted by .NET into `report_ai_content.Indicators`
    / `report_ai_content.Trends` (jsonb columns).
    """
    not_ready = await _ensure_ingested(body.report_id, conn)
    if not_ready:
        raise HTTPException(status_code=202, detail=not_ready)

    pages = await _fetch_pages_aggregated(conn, body.report_id)
    # to_thread: extract_insights is sync and shares the same retry/sleep
    # path as summarize — keep it off the event loop.
    result = await asyncio.to_thread(
        extract_insights, [content for _, content in pages],
    )
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
      1. Numerical similarity — pairwise cosine on each report's mean chunk
         embedding. Cheap, runs in pgvector + numpy, gives a 0..1 score per pair.
      2. Qualitative comparison — sends the stored summaries + key findings of
         each report to Gemini for structured output (common topics, key
         differences, shared indicators).

    Each report must already be ingested AND summarized.
    """
    if len(body.report_ids) < 2:
        raise HTTPException(status_code=400, detail="Need at least 2 report_ids to compare")

    # Auto-queue any reports that haven't been ingested yet. Returns 202 with
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

    # 1. Mean chunk embedding per report — done in numpy, since pgvector's avg()
    #    aggregate isn't universally available.
    emb_cur = await conn.execute(
        'SELECT "ReportId", embedding FROM report_chunks '
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

    # 2. Pull summary + key_findings from report_ai_contents for each report.
    ai_cur = await conn.execute(
        '''
        SELECT "ReportId", "Summary", "KeyFindings"
        FROM report_ai_contents
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

    # Decide the comparison's output language from the inputs:
    #   all reports same language → that language
    #   any mismatch / unknown    → Arabic (project default + product spec:
    #                               Arabic is the lingua franca of the audience)
    lang_cur = await conn.execute(
        'SELECT "OriginalLanguage" FROM reports WHERE "Id" = ANY(%s)',
        [ids],
    )
    lang_rows = await lang_cur.fetchall()
    langs = {(row[0] or "").lower() for row in lang_rows if row[0]}
    target_language = next(iter(langs)) if len(langs) == 1 else "ar"

    # to_thread: same reason as summarize / insights — sync gemini call
    # with a long retry path must not block the event loop.
    qualitative = await asyncio.to_thread(
        compare_reports, reports_for_gemini, language=target_language,
    )

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

    Language selection:
      - If `source_language` and `target_language` are both provided in the
        request, those are used verbatim.
      - If only one is provided, the other is auto-derived (Arabic ↔ English).
      - If neither is provided, source is detected from the report's stored
        chunks (handles glyph-form Arabic correctly) and target is the
        opposite of Arabic vs English.
    """
    not_ready = await _ensure_ingested(body.report_id, conn)
    if not_ready:
        raise HTTPException(status_code=202, detail=not_ready)

    # Per-org daily translation quota — Google Translate is per-character
    # paid, so a chatty caller can run up real bills here. Look up org via
    # the report and gate before we hit the Translation API.
    org_cur = await conn.execute(
        'SELECT "OrganizationId" FROM reports WHERE "Id" = %s',
        [str(body.report_id)],
    )
    org_row = await org_cur.fetchone()
    organization_id = org_row[0] if org_row else None
    await assert_under_job_quota(conn, organization_id, "Translation")

    output_prefix = body.output_prefix
    if not output_prefix.startswith("gs://"):
        raise HTTPException(status_code=400, detail="output_prefix must start with gs://")
    if not output_prefix.endswith("/"):
        output_prefix += "/"

    source_language, target_language = await _resolve_languages(
        conn, body.report_id, body.source_language, body.target_language,
    )

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


async def _resolve_languages(
    conn: AsyncConnection,
    report_id: UUID,
    source_override: str | None,
    target_override: str | None,
) -> tuple[str, str]:
    """Decide (source, target) language given optional caller overrides.

    Reads from report_chunks only when at least one of the languages still
    needs to be detected — saves a query when both are passed explicitly."""
    if source_override and target_override:
        if source_override == target_override:
            raise HTTPException(
                status_code=400,
                detail="source_language and target_language must differ",
            )
        return source_override, target_override

    # Need to detect at least one — sample chunks once.
    if not source_override:
        cur = await conn.execute(
            '''
            SELECT "Content" FROM report_chunks
            WHERE "ReportId" = %s
            ORDER BY "PageNumber", "ChunkIndex"
            LIMIT 10
            ''',
            [str(report_id)],
        )
        rows = await cur.fetchall()
        sample = " ".join(r[0] for r in rows if r[0])
        source_language = detect_source_language(sample)
    else:
        source_language = source_override

    target_language = target_override or ("en" if source_language == "ar" else "ar")

    if source_language == target_language:
        raise HTTPException(
            status_code=400,
            detail=f"source_language ({source_language}) and target_language match",
        )
    return source_language, target_language


# ── Bulk endpoints ───────────────────────────────────────────────────────────
#
# Pattern (post-worker-split):
#   1. Client POSTs items → we INSERT one ai_jobs row per item (status=Pending)
#   2. Workers claim and process rows independently via FOR UPDATE SKIP LOCKED.
#   3. We return 202 with the list of job_ids — client polls GET /jobs/{id}.
#
# Why: 50+ items × ~30 s each easily exceeds Cloud Run's 5 min request timeout,
# and previously each bulk POST also tied background work to the API instance's
# lifecycle (a recycled instance left half-done jobs Processing forever).
# Worker isolation removes both problems.

@router.post(
    "/bulk/ingest",
    response_model=BulkJobsResponse,
    status_code=202,
)
async def bulk_ingest(
    body: BulkIngestRequest,
    conn: AsyncConnection = Depends(get_conn),
):
    """Queue ingest jobs for many reports — GPU-direct, no worker involved.

    Each item triggers `doc_extractor.trigger_ingest` exactly like the
    single-report `/ingest` endpoint, so all bulk ingests run on the
    doc-processor GPU container in parallel (the GPU pool autoscales as
    requests arrive). The worker is never involved.

    To also generate summaries for these reports, call `/bulk/summarize`
    once each ingest reaches Completed (or accept that the .NET side will
    enqueue Summarization jobs separately).

    Returns 202 with the list of job_ids — clients poll
    GET /api/ai/reports/jobs/{job_id} per row.
    """
    created: list[CreatedJob] = []
    for item in body.items:
        job_id = uuid4()
        await insert_job(
            conn,
            job_id=job_id,
            job_type="Ingestion",
            report_id=item.report_id,
            input_data={"file_url": item.file_url, "step": "ingest"},
        )
        created.append(CreatedJob(job_id=job_id, report_id=item.report_id))
    await conn.commit()

    # Bounded-concurrency dispatch (2026-05-08): /v1/ingest_job is now
    # synchronous, so each trigger holds its HTTP connection open until
    # that job's pipeline finishes. We cap simultaneous triggers at
    # doc_processor_max_concurrency (= doc-processor's --max-instances)
    # so Cloud Run autoscales horizontally to N pods without creating
    # queue depth it can't scale into. As each job finishes, the next
    # trigger fires.
    asyncio.create_task(
        _dispatch_bulk_ingest(
            [(str(cj.job_id), str(item.report_id), item.file_url)
             for cj, item in zip(created, body.items)],
        ),
    )

    return BulkJobsResponse(jobs=created)


async def _dispatch_bulk_ingest(
    jobs: list[tuple[str, str, str]],
) -> None:
    """Fire ingest triggers with bounded concurrency = max_instances.

    Runs as an asyncio.create_task off the bulk_ingest endpoint, so the
    HTTP response (with the list of job_ids) doesn't have to wait for
    the actual pipelines to finish. Each trigger holds its httpx
    connection open until the doc-processor finishes that job; the
    semaphore caps how many run in parallel.

    Failures are logged and the job is left Pending — the
    _pending_job_watcher (main.py) re-fires it within 60 s, so this
    function doesn't need its own retry logic.
    """
    sem = asyncio.Semaphore(settings.doc_processor_max_concurrency)
    started = time.time()
    logger.info(
        "[bulk_ingest] dispatching %d jobs with concurrency=%d",
        len(jobs), settings.doc_processor_max_concurrency,
    )

    async def _one(job_id: str, report_id: str, file_url: str) -> None:
        async with sem:
            await doc_extractor.trigger_ingest(job_id, report_id, file_url)

    await asyncio.gather(
        *(_one(j, r, u) for j, r, u in jobs),
        return_exceptions=True,
    )
    logger.info(
        "[bulk_ingest] dispatch finished — %d jobs in %.1fs",
        len(jobs), time.time() - started,
    )


@router.post(
    "/bulk/summarize",
    response_model=BulkJobsResponse,
    status_code=202,
)
async def bulk_summarize(
    body: BulkSummarizeRequest,
    conn: AsyncConnection = Depends(get_conn),
):
    """Queue Summarization jobs for many already-ingested reports.

    Each row inserts a `JobType=Summarization` ai_jobs entry that the
    worker picks up on its next poll cycle. The worker calls
    `summarize_report` (combined summary + insights) and writes all five
    output fields into `ai_jobs.OutputData`. Pre-condition: every report
    in `items` must already have rows in `report_chunks` — call
    `/bulk/ingest` first if not.

    Returns 202 with the list of job_ids — clients poll
    GET /api/ai/reports/jobs/{job_id} per row.
    """
    created: list[CreatedJob] = []
    for item in body.items:
        job_id = uuid4()
        await insert_job(
            conn,
            job_id=job_id,
            job_type="Summarization",
            report_id=item.report_id,
            input_data={"step": "summarize"},
        )
        created.append(CreatedJob(job_id=job_id, report_id=item.report_id))
    await conn.commit()

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
    """Queue translate jobs for many reports. Returns immediately."""
    created: list[CreatedJob] = []
    for item in body.items:
        job_id = uuid4()
        await insert_job(
            conn,
            job_id=job_id,
            job_type="Translation",
            report_id=item.report_id,
            input_data={"file_url": item.file_url, "output_prefix": item.output_prefix},
        )
        created.append(CreatedJob(job_id=job_id, report_id=item.report_id))
    await conn.commit()

    return BulkJobsResponse(jobs=created)


@router.get("/jobs/{job_id}", response_model=JobStatusResponse)
async def get_job(
    job_id: UUID,
    conn: AsyncConnection = Depends(get_conn),
):
    """Poll the status of a previously-queued job."""
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
