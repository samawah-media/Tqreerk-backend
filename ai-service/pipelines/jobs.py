"""Job orchestration — shared between API endpoints and the polling worker.

The original architecture spawned in-process `asyncio.create_task(...)` after
each /reports/ingest, /reports/bulk/* etc. That tied background work to the
lifecycle of the request-serving Cloud Run instance:
    • Ingest sat on the same event loop as chat streaming → a 100-page PDF
      ate CPU and Gemini-call slots that should have served chat traffic.
    • If Cloud Run recycled the instance mid-job, the task died silently and
      ai_jobs stayed in 'Processing' forever (TODO at api/reports.py:441-443).

The new pattern:
    1. API endpoints only INSERT a row into ai_jobs (status='Pending') and
       return immediately. No spawning.
    2. A separate Cloud Run service runs WORKER_MODE=worker. It polls ai_jobs
       with `FOR UPDATE SKIP LOCKED`, claims one Pending row, runs the job,
       and commits. Multiple worker replicas are safe to run in parallel.
    3. A periodic sweeper marks jobs stuck in 'Processing' beyond a threshold
       as 'Failed' so a crashed worker doesn't leave dead rows.

This module exposes the worker functions (`run_*_job`) and primitive helpers
that both the API endpoints and the worker loop share.
"""
import asyncio
import json
import logging
import math
from datetime import datetime, timezone
from uuid import UUID

import sentry_sdk
from psycopg import AsyncConnection

from core.config import settings
from core.db import conn_ctx
from services.gemini import summarize_report
from services.translate import detect_source_language, translate_pdf

logger = logging.getLogger(__name__)


def _clean_nan(obj):
    """Recursively replace float NaN / Inf with None before JSON serialization.

    Python's json.dumps emits the literal token `NaN` for float('nan') which
    is not valid JSON and causes PostgreSQL to reject the value with:
        "invalid input syntax for type json: Token 'NaN' is invalid."

    This is most visible when Ragas cannot compute a metric (e.g. because the
    judge LLM timed out) and returns float('nan') as the score.
    """
    if isinstance(obj, float) and (math.isnan(obj) or math.isinf(obj)):
        return None
    if isinstance(obj, dict):
        return {k: _clean_nan(v) for k, v in obj.items()}
    if isinstance(obj, list):
        return [_clean_nan(v) for v in obj]
    return obj


# ── ai_jobs table primitives ─────────────────────────────────────────────────

async def insert_job(
    conn: AsyncConnection,
    job_id: UUID,
    job_type: str,
    report_id: UUID,
    input_data: dict,
    organization_id: UUID | str | None = None,
) -> None:
    """Add a Pending row to ai_jobs. Caller commits.

    `organization_id` is used for per-org daily quota enforcement and is
    written onto the row (matching .NET's EnqueueIngestAsync behaviour).
    When omitted, we derive it from the report so callers don't have to
    plumb it through. Internal jobs (Evaluation) explicitly pass
    organization_id=None to skip the quota check — they're not user-
    initiated and gated upstream by the chat quota.
    """
    # Derive org_id when the caller didn't pass one. Cheap one-row PK lookup.
    if organization_id is None and job_type != "Evaluation":
        try:
            cur = await conn.execute(
                'SELECT "OrganizationId" FROM reports WHERE "Id" = %s',
                [str(report_id)],
            )
            row = await cur.fetchone()
            if row and row[0] is not None:
                organization_id = row[0]
        except Exception as exc:
            logger.warning(
                "[quota] could not look up org_id for report=%s: %s",
                report_id, exc,
            )

    # Quota gate — raises HTTPException(429) if the org has hit its cap.
    # No-op when org_id is unknown (e.g. Evaluation) or quotas disabled.
    from services.quota import assert_under_job_quota
    await assert_under_job_quota(conn, organization_id, job_type)

    await conn.execute(
        """
        INSERT INTO ai_jobs ("Id", "ReportId", "OrganizationId", "JobType",
                             "Status", "TokensUsed", "InputData", "CreatedAt")
        VALUES (%s, %s, %s, %s, 'Pending', 0, %s, now())
        """,
        [
            str(job_id),
            str(report_id),
            str(organization_id) if organization_id else None,
            job_type,
            json.dumps(input_data),
        ],
    )
    # Wake the worker if it's scaled to zero. Fire-and-forget — a failure here
    # just means the worker stays asleep until its next cold-start trigger.
    asyncio.create_task(_wake_worker())


async def _wake_worker() -> None:
    """Ping the worker's /health endpoint to wake a scaled-to-zero instance.

    The worker is deployed with --no-allow-unauthenticated so we attach a
    Google-signed ID token (audience = worker URL). Cloud Run validates the
    token at the load balancer; on auth failure the container never starts,
    which would defeat the whole point of this wake-up.

    Timeout sizing: fire-and-forget ping. The HTTP request hitting the Cloud
    Run frontend is what triggers the worker's cold start, even if our own
    request times out before the container is ready to respond. So a
    timeout here is a "false negative" on the log (warning fires, but the
    wake actually succeeded). 15 s is generous enough to ride out the
    typical 3-8 s Python cold start AND the ~500 ms ID-token fetch, so
    when a real failure does fire we can trust it.
    """
    url = settings.worker_url
    if not url:
        logger.info("[wake] WORKER_URL not set, skipping wake-up")
        return
    try:
        # google.auth's fetch_id_token is sync; offload so we don't block
        # the API request that scheduled this fire-and-forget task.
        from google.auth.transport.requests import Request as AuthRequest
        from google.oauth2 import id_token as oauth_id_token

        loop = asyncio.get_running_loop()
        token = await loop.run_in_executor(
            None,
            lambda: oauth_id_token.fetch_id_token(AuthRequest(), url),
        )
        headers = {"Authorization": f"Bearer {token}"} if token else {}

        import httpx
        async with httpx.AsyncClient(timeout=60) as client:
            resp = await client.get(url.rstrip("/") + "/health", headers=headers)
        logger.info("[wake] pinged %s -> %d", url, resp.status_code)
    except Exception as exc:
        # Include the exception class name — many httpx / auth exceptions
        # stringify to an empty body, which makes "failed pinging URL: "
        # logs unactionable. The class name disambiguates between cold-
        # start timeout (ReadTimeout / ConnectTimeout — usually a false
        # negative; the wake still happened), auth failure
        # (DefaultCredentialsError, RefreshError), and DNS / network
        # errors.
        logger.warning(
            "[wake] failed pinging %s (%s): %s",
            url, type(exc).__name__, exc or repr(exc),
        )


async def mark_processing(job_id: UUID) -> None:
    async with conn_ctx() as conn:
        await conn.execute(
            'UPDATE ai_jobs SET "Status"=\'Processing\', "StartedAt"=now() WHERE "Id"=%s',
            [str(job_id)],
        )
        await conn.commit()


async def mark_completed(job_id: UUID, output: dict) -> None:
    async with conn_ctx() as conn:
        await conn.execute(
            """
            UPDATE ai_jobs
            SET "Status"='Completed', "OutputData"=%s, "CompletedAt"=now()
            WHERE "Id"=%s
            """,
            [json.dumps(_clean_nan(output)), str(job_id)],
        )
        await conn.commit()


async def mark_failed(job_id: UUID, error: str) -> None:
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


# ── Job runners ─────────────────────────────────────────────────────────────
# Each runner takes a single ai_jobs row and drives it to terminal state.
# They MUST be safe to call from either the worker loop or (legacy path) an
# in-process task — i.e. they always mark Processing → Completed/Failed and
# never raise back to the caller.

async def run_summarize_job(job_id: UUID, report_id: UUID) -> None:
    """Generate the combined summary + insights for an already-ingested report.

    Runs in the worker, mirrors `run_translate_job`'s shape: claim, do work,
    write all 5 output fields (summary, key_findings, topics, indicators,
    trends) into ai_jobs.OutputData. The C# finalizer
    (CopySummaryToContentAsync) then copies them across to
    report_ai_contents.

    Pre-condition: the report's chunks must already exist in report_chunks
    (i.e. ingest has completed). If chunks are missing the job fails fast so
    the operator can re-run after the ingest finishes — we don't want to
    silently retry-loop while ingest catches up.
    """
    logger.info("[job %s] summarize start report=%s", job_id, report_id)
    await mark_processing(job_id)
    try:
        async with conn_ctx() as conn:
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
            page_rows = await cur.fetchall()

            # Pin the summary's output language to the report's OriginalLanguage
            # — Gemini's auto-detection is unreliable on mixed AR/EN PDFs and
            # was returning English for Arabic reports.
            lang_cur = await conn.execute(
                'SELECT "OriginalLanguage" FROM reports WHERE "Id" = %s',
                [str(report_id)],
            )
            lang_row = await lang_cur.fetchone()
            language = (lang_row[0] if lang_row and lang_row[0] else "ar").lower()

        if not page_rows:
            raise RuntimeError(
                "Report has no chunks — ingest must run before summarize"
            )

        # to_thread so the sync retry/sleep loop in gemini.py doesn't
        # block the event loop. See ai-service/api/reports.py for the
        # matching change in the HTTP path.
        summary = await asyncio.to_thread(
            summarize_report,
            [content for _, content in page_rows],
            language=language,
        )

        logger.info(
            "[job %s] summarize done: %d findings, %d topics, %d indicators, %d trends",
            job_id, len(summary.key_findings), len(summary.topics),
            len(summary.indicators), len(summary.trends),
        )

        await mark_completed(job_id, {
            "summary":      summary.summary,
            "key_findings": summary.key_findings,
            "topics":       summary.topics,
            "indicators":   [i.model_dump(mode="json") for i in summary.indicators],
            "trends":       [t.model_dump(mode="json") for t in summary.trends],
        })
    except Exception as exc:
        logger.exception("[job %s] summarize FAILED: %s", job_id, exc)
        await mark_failed(job_id, str(exc))


async def run_translate_job(
    job_id: UUID, report_id: UUID, file_url: str, output_prefix: str,
) -> None:
    logger.info(
        "[job %s] translate start report=%s file=%s prefix=%s",
        job_id, report_id, file_url, output_prefix,
    )
    await mark_processing(job_id)
    try:
        if not output_prefix.startswith("gs://"):
            raise ValueError("output_prefix must start with gs://")
        prefix = output_prefix if output_prefix.endswith("/") else output_prefix + "/"

        async with conn_ctx() as conn:
            # Sample enough chunks to outweigh a cover-page bias. 10 chunks
            # is ~5000 chars which is well past the point where Arabic vs
            # Latin counts stabilise.
            cur = await conn.execute(
                """
                SELECT "Content" FROM report_chunks
                WHERE "ReportId" = %s
                ORDER BY "PageNumber", "ChunkIndex"
                LIMIT 10
                """,
                [str(report_id)],
            )
            rows = await cur.fetchall()
            if not rows:
                raise RuntimeError("Report chunks not found — ingest first")
            sample = " ".join(r[0] for r in rows if r[0])

        # Detect locally using char-count over BOTH standard Arabic and the
        # Presentation Forms blocks. Older ingests stored Arabic as glyph
        # forms (U+FE70–FEFF, U+FB50–FDFF) and Google's detect_language API —
        # along with our doc-processor metadata heuristic — both miss that
        # case and call them English. Counting all four Arabic blocks gets
        # the right answer regardless of how the text was extracted.
        source_language = detect_source_language(sample)
        logger.info(
            "[job %s] source_language=%s from %d-char sample",
            job_id, source_language, len(sample),
        )

        target_language = "en" if source_language == "ar" else "ar"

        logger.info(
            "[job %s] translate %s -> %s, calling Cloud Translation",
            job_id, source_language, target_language,
        )
        translated_url = translate_pdf(
            gcs_input_uri=file_url,
            output_prefix=prefix,
            source_language=source_language,
            target_language=target_language,
        )
        logger.info("[job %s] translate done -> %s", job_id, translated_url)
        await mark_completed(job_id, {
            "source_language": source_language,
            "target_language": target_language,
            "translated_file_url": translated_url,
        })
    except Exception as exc:
        logger.exception("[job %s] translate FAILED: %s", job_id, exc)
        await mark_failed(job_id, str(exc))


async def run_eval_job(
    job_id: UUID, report_id: UUID, raw_input: dict,
) -> None:
    """Run Ragas metrics on a finished chat and post Langfuse scores.

    InputData shape produced by api/chat.py at end-of-stream:
        {
          "trace_id":  str,             # Langfuse trace to score
          "question":  str,
          "contexts":  list[str],        # the rerank-survivor chunks
          "answer":    str,
          "session_id": str,             # optional, for log context
        }

    The job never raises — eval is advisory. Failures are logged, marked
    Failed in ai_jobs (so retries don't auto-fire), and a single
    `eval_error` score is posted to Langfuse so dashboards see the gap.
    """
    trace_id = raw_input.get("trace_id") or ""
    question = raw_input.get("question") or ""
    contexts = raw_input.get("contexts") or []
    answer = raw_input.get("answer") or ""

    logger.info(
        "[job %s] eval start report=%s trace=%s n_ctx=%d",
        job_id, report_id, trace_id, len(contexts),
    )

    if not (question and contexts and answer):
        await mark_failed(job_id, "eval job missing question/contexts/answer")
        return

    await mark_processing(job_id)
    try:
        from pipelines.eval import run_eval

        scores = await run_eval(
            question=question,
            contexts=list(contexts),
            answer=answer,
            trace_id=trace_id or None,
        )
        logger.info("[job %s] eval done scores=%s", job_id, scores)
        await mark_completed(job_id, {
            "scores": scores,
            "trace_id": trace_id,
        })
    except Exception as exc:
        logger.exception("[job %s] eval FAILED: %s", job_id, exc)
        await mark_failed(job_id, str(exc))


# ── Dispatch — picks a runner based on the ai_jobs row ───────────────────────

async def dispatch(job: dict) -> None:
    """Run the job to terminal state based on its JobType + InputData payload."""
    job_id = UUID(str(job["id"]))
    report_id_raw = job.get("report_id")
    if report_id_raw is None:
        await mark_failed(job_id, "Job has no report_id — cannot dispatch")
        return
    report_id = UUID(str(report_id_raw))

    raw_input = job.get("input_data") or {}
    if isinstance(raw_input, str):
        try:
            raw_input = json.loads(raw_input)
        except json.JSONDecodeError:
            raw_input = {}

    job_type = job.get("job_type", "")
    step = (raw_input.get("step") or "").lower()
    file_url = raw_input.get("file_url")
    extractor = (raw_input.get("extractor") or "auto").lower()

    if job_type == "Ingestion":
        # All ingest jobs run on the GPU service (doc-processor) directly —
        # the API fires `trigger_ingest` at enqueue time. The worker should
        # never see an Ingestion row pass its claim filter, but we keep the
        # branch so a malformed row fails loudly instead of silently looping.
        await mark_failed(
            job_id,
            "Ingestion jobs are GPU-owned; the worker should not dispatch them. "
            "If you see this error, check that /reports/ingest is firing "
            "doc_extractor.trigger_ingest at enqueue time.",
        )
        return

    if job_type == "Summarization":
        await run_summarize_job(job_id, report_id)
        return

    if job_type == "Translation":
        output_prefix = raw_input.get("output_prefix")
        if not file_url or not output_prefix:
            await mark_failed(
                job_id,
                "Translation job missing file_url / output_prefix in input_data",
            )
            return
        await run_translate_job(job_id, report_id, file_url, output_prefix)
        return

    if job_type == "Evaluation":
        # Online RAG eval — runs Ragas metrics on a finished chat and posts
        # the scores back onto its Langfuse trace. Best-effort by design;
        # never retried, never blocks anything.
        await run_eval_job(job_id, report_id, raw_input)
        return

    await mark_failed(job_id, f"Unknown JobType '{job_type}'")


# ── Worker loop ──────────────────────────────────────────────────────────────

async def claim_n_jobs(conn: AsyncConnection, n: int) -> list[dict]:
    """Atomically claim up to n Pending jobs. Uses FOR UPDATE SKIP LOCKED so
    multiple worker replicas can poll concurrently without dogpiling on the
    same rows.

    Returns a list of up to n claimed rows (empty list if nothing is pending).
    Each row is moved to 'Processing' atomically in the same UPDATE so no
    other worker can claim the same job."""
    cur = await conn.execute(
        """
        WITH next_jobs AS (
            SELECT "Id"
            FROM ai_jobs
            WHERE "Status" = 'Pending'
              AND "InputData" IS NOT NULL
              AND "InputData" != 'null'::jsonb
              -- file_url is required for Translation; Evaluation has its own
              -- input shape (trace_id + chat); Summarization works off the
              -- already-ingested report_chunks rows so no file_url is needed.
              AND ("InputData"->>'file_url' IS NOT NULL
                   OR "JobType" IN ('Evaluation', 'Summarization'))
              -- All Ingestion jobs run on the GPU service (doc-processor)
              -- directly via /v1/ingest_job. The worker never claims them so
              -- it can never race with the GPU for the same row.
              AND "JobType" <> 'Ingestion'
            ORDER BY "CreatedAt"
            FOR UPDATE SKIP LOCKED
            LIMIT %s
        )
        UPDATE ai_jobs
        SET "Status" = 'Processing', "StartedAt" = now()
        FROM next_jobs
        WHERE ai_jobs."Id" = next_jobs."Id"
        RETURNING ai_jobs."Id", ai_jobs."ReportId", ai_jobs."JobType",
                  ai_jobs."InputData"
        """,
        [n],
    )
    rows = await cur.fetchall()
    await conn.commit()
    return [
        {
            "id": row[0],
            "report_id": row[1],
            "job_type": row[2],
            "input_data": row[3],
        }
        for row in rows
    ]


async def sweep_stale_jobs() -> int:
    """Mark jobs stuck in 'Processing' beyond worker_stale_job_minutes as Failed.

    Called periodically from the worker loop. Closes the failure mode where a
    Cloud Run instance gets recycled mid-job and leaves a dead 'Processing'
    row that no future worker will ever pick up (because polling only looks
    for 'Pending').
    """
    threshold = settings.worker_stale_job_minutes
    async with conn_ctx() as conn:
        cur = await conn.execute(
            """
            UPDATE ai_jobs
            SET "Status"      = 'Failed',
                "ErrorMessage" = COALESCE("ErrorMessage", '') ||
                                 'Worker did not finish within ' || %s ||
                                 ' minutes — marked stale by sweeper.',
                "CompletedAt" = now()
            WHERE "Status" = 'Processing'
              AND "StartedAt" IS NOT NULL
              AND "StartedAt" < now() - (%s || ' minutes')::interval
            """,
            [str(threshold), str(threshold)],
        )
        await conn.commit()
        return cur.rowcount or 0


async def run_worker_loop(shutdown: asyncio.Event | None = None) -> None:
    """Main worker entrypoint. Polls ai_jobs forever; runs up to
    `worker_concurrency` jobs concurrently per loop iteration.

    Concurrency model:
        • Claims up to N Pending rows atomically (FOR UPDATE SKIP LOCKED).
        • Dispatches all N with asyncio.gather — each job runs in a separate
          thread (asyncio.to_thread) so Gemini calls happen truly in parallel.
        • Multiple worker replicas stay safe: SKIP LOCKED means each instance
          claims a disjoint set of rows.
        • Stale-job sweep runs every loop iteration but is cheap (single
          UPDATE that no-ops when nothing is stale).

    shutdown: asyncio.Event set by the SIGTERM handler in main.py. When set,
        the loop waits for any in-flight gather to finish, then exits cleanly.
    """
    poll = settings.worker_poll_interval_seconds
    concurrency = max(1, settings.worker_concurrency)
    logger.info(
        "worker started (poll_interval=%.1fs, concurrency=%d, stale_after=%dm)",
        poll, concurrency, settings.worker_stale_job_minutes,
    )

    last_sweep = 0.0
    sweep_every_seconds = 60.0

    while True:
        # Honour a shutdown request between jobs — never interrupt a running job.
        if shutdown and shutdown.is_set():
            logger.info("Worker shutdown event set — exiting loop after idle check")
            return

        try:
            async with conn_ctx() as conn:
                jobs = await claim_n_jobs(conn, concurrency)
        except Exception as exc:
            logger.exception("worker claim_n_jobs failed: %s", exc)
            await asyncio.sleep(poll)
            continue

        # Periodic stale-job sweep — runs at most once a minute regardless of
        # poll interval, so burst polling doesn't hammer the table.
        now = datetime.now(timezone.utc).timestamp()
        if now - last_sweep > sweep_every_seconds:
            last_sweep = now
            try:
                n = await sweep_stale_jobs()
                if n:
                    logger.warning("worker swept %d stale Processing jobs", n)
            except Exception as exc:
                logger.exception("worker stale-job sweep failed: %s", exc)

        if not jobs:
            if shutdown and shutdown.is_set():
                logger.info("Worker shutdown event set and queue empty — exiting")
                return
            await asyncio.sleep(poll)
            continue

        for job in jobs:
            raw = job.get("input_data") or {}
            if isinstance(raw, str):
                try:
                    raw = json.loads(raw)
                except Exception:
                    raw = {}
            step = raw.get("step") or ""
            logger.info(
                "worker claimed job=%s type=%s step=%s report=%s",
                job["id"], job["job_type"], step or "(none)", job["report_id"],
            )

        async def _safe_dispatch(job: dict) -> None:
            try:
                await dispatch(job)
            except Exception as exc:
                logger.exception(
                    "worker dispatch raised for job %s: %s", job["id"], exc,
                )
                try:
                    await mark_failed(UUID(str(job["id"])), f"Dispatcher crashed: {exc}")
                except Exception:
                    pass

        # Run all claimed jobs concurrently. Each dispatch call uses
        # asyncio.to_thread internally (Gemini is a sync blocking call),
        # so they truly run in parallel OS threads.
        await asyncio.gather(*(_safe_dispatch(job) for job in jobs))
