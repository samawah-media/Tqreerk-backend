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
from datetime import datetime, timezone
from uuid import UUID

import sentry_sdk
from psycopg import AsyncConnection

from core.config import settings
from core.db import conn_ctx
from pipelines.ingest import ingest_report
from services.gemini import summarize_report
from services.translate import detect_source_language, translate_pdf

logger = logging.getLogger(__name__)


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
        async with httpx.AsyncClient(timeout=15) as client:
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
            [json.dumps(output), str(job_id)],
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

async def run_ingest_only_job(
    job_id: UUID, report_id: UUID, file_url: str,
    extractor: str = "auto",
) -> None:
    logger.info(
        "[job %s] ingest start report=%s file=%s extractor=%s",
        job_id, report_id, file_url, extractor,
    )
    with sentry_sdk.start_transaction(op="ingest", name="ingest_report", sampled=True) as txn:
        txn.set_tag("report_id", str(report_id))
        txn.set_tag("job_id", str(job_id))
        txn.set_tag("extractor", extractor)
        txn.set_data("file_url", file_url)
        await mark_processing(job_id)
        try:
            result = await ingest_report(report_id, file_url, extractor=extractor)
            logger.info("[job %s] ingest done %s", job_id, result)
            txn.set_data("result", result)
            txn.set_tag("extractor_used", result.get("extractor", "unknown"))
            await mark_completed(job_id, result)
        except Exception as exc:
            sentry_sdk.capture_exception(exc)
            logger.exception("[job %s] ingest FAILED: %s", job_id, exc)
            await mark_failed(job_id, str(exc))


async def run_ingest_summarize_job(
    job_id: UUID, report_id: UUID, file_url: str,
    extractor: str = "auto",
) -> None:
    logger.info(
        "[job %s] ingest+summarize start report=%s file=%s extractor=%s",
        job_id, report_id, file_url, extractor,
    )
    with sentry_sdk.start_transaction(op="ingest", name="ingest_summarize_report", sampled=True) as txn:
        txn.set_tag("report_id", str(report_id))
        txn.set_tag("job_id", str(job_id))
        txn.set_tag("extractor", extractor)
        txn.set_data("file_url", file_url)
        await mark_processing(job_id)
        try:
            ingest_result = await ingest_report(report_id, file_url, extractor=extractor)
            logger.info("[job %s] ingest phase done: %s", job_id, ingest_result)
            txn.set_tag("extractor_used", ingest_result.get("extractor", "unknown"))

            with sentry_sdk.start_span(op="ingest.summarize", description="summarize_report"):
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

                if not page_rows:
                    raise RuntimeError("Ingest produced no pages")

                summary = summarize_report([content for _, content in page_rows])

            logger.info(
                "[job %s] summarize done: %d findings, %d topics",
                job_id, len(summary.key_findings), len(summary.topics),
            )
            result = {
                **ingest_result,
                "summary": summary.summary,
                "key_findings": summary.key_findings,
                "topics": summary.topics,
            }
            txn.set_data("result", {k: v for k, v in result.items() if k != "summary"})
            await mark_completed(job_id, result)
        except Exception as exc:
            sentry_sdk.capture_exception(exc)
            logger.exception("[job %s] ingest+summarize FAILED: %s", job_id, exc)
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
        if not file_url:
            await mark_failed(job_id, "Ingestion job missing file_url in input_data")
            return
        if step == "ingest":
            await run_ingest_only_job(job_id, report_id, file_url, extractor=extractor)
        else:
            
            await run_ingest_summarize_job(job_id, report_id, file_url, extractor=extractor)
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

async def claim_one_job(conn: AsyncConnection) -> dict | None:
    """Atomically claim the next Pending job. Uses FOR UPDATE SKIP LOCKED so
    multiple worker replicas can poll concurrently without dogpiling on a
    single row.

    Returns the claimed row as a dict, or None if no Pending rows exist.
    The row is left in 'Pending' status — the runner moves it to 'Processing'
    via mark_processing(); the SKIP LOCKED contract is sufficient because the
    transaction holding the row lock fences out other workers."""
    cur = await conn.execute(
        """
        WITH next_job AS (
            SELECT "Id"
            FROM ai_jobs
            WHERE "Status" = 'Pending'
              AND "InputData" IS NOT NULL
              AND "InputData" != 'null'::jsonb
              AND ("InputData"->>'file_url' IS NOT NULL
                   OR "JobType" = 'Evaluation')
              -- step=ingest jobs are handled directly by the GPU service
              -- (doc-processor /v1/ingest_job). Exclude them so the worker
              -- never races with the GPU for the same row.
              AND NOT (
                    "JobType" = 'Ingestion'
                    AND "InputData"->>'step' = 'ingest'
              )
            ORDER BY "CreatedAt"
            FOR UPDATE SKIP LOCKED
            LIMIT 1
        )
        UPDATE ai_jobs
        SET "Status" = 'Processing', "StartedAt" = now()
        FROM next_job
        WHERE ai_jobs."Id" = next_job."Id"
        RETURNING ai_jobs."Id", ai_jobs."ReportId", ai_jobs."JobType",
                  ai_jobs."InputData"
        """,
    )
    row = await cur.fetchone()
    await conn.commit()
    if not row:
        return None
    return {
        "id": row[0],
        "report_id": row[1],
        "job_type": row[2],
        "input_data": row[3],
    }


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
    """Main worker entrypoint. Polls ai_jobs forever; runs one job at a time.

    Concurrency model:
        • This task runs on its own Cloud Run instance (WORKER_MODE=worker).
        • One job at a time per instance — keeps Gemini RPM predictable. Scale
          horizontally by adding instances; each picks a different job thanks
          to FOR UPDATE SKIP LOCKED.
        • Stale-job sweep runs every loop iteration but is cheap (single
          UPDATE that no-ops when nothing is stale).

    shutdown: asyncio.Event set by the SIGTERM handler in main.py. When set,
        the loop finishes the current job (if any) and exits cleanly instead
        of being killed mid-job.  Passing None falls back to the old
        "loop forever" behaviour (used in tests / local runs).
    """
    poll = settings.worker_poll_interval_seconds
    logger.info(
        "worker started (poll_interval=%.1fs, stale_after=%dm)",
        poll, settings.worker_stale_job_minutes,
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
                job = await claim_one_job(conn)
        except Exception as exc:
            logger.exception("worker claim_one_job failed: %s", exc)
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

        if job is None:
            if shutdown and shutdown.is_set():
                logger.info("Worker shutdown event set and queue empty — exiting")
                return
            await asyncio.sleep(poll)
            continue

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
        try:
            await dispatch(job)
        except Exception as exc:
            # dispatch should never raise (each runner catches its own
            # exceptions and marks the row Failed) but if it ever does,
            # we don't want a single bad job to kill the worker loop.
            logger.exception(
                "worker dispatch raised for job %s: %s", job["id"], exc,
            )
            try:
                await mark_failed(UUID(str(job["id"])), f"Dispatcher crashed: {exc}")
            except Exception:
                pass
