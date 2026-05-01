"""Process entrypoint — selects between FastAPI server and background worker.

Modes
-----
WORKER_MODE = "api"     → uvicorn boots `app` (the default Cloud Run service).
WORKER_MODE = "worker"  → an asyncio loop polls ai_jobs and runs ingest /
                          translate jobs on a separate Cloud Run service.

Both modes share the same image; the deployment just sets WORKER_MODE so
each Cloud Run service autoscales independently. The API stays responsive
under heavy ingest load because page-rendering / Gemini Vision runs on the
worker pool, not on the request-serving instance.
"""
import asyncio
import logging
import signal
import uuid
from contextlib import asynccontextmanager

# Sentry must initialize BEFORE FastAPI/Starlette are imported so its
# auto-instrumentation can hook into them. If SENTRY_DSN is not set, this
# becomes a no-op and the rest of the app runs normally.
import sentry_sdk

from core.config import settings

if settings.sentry_dsn:
    sentry_sdk.init(
        dsn=settings.sentry_dsn,
        environment=settings.environment,
        traces_sample_rate=0.1,        # 10% of requests get a perf trace
        profiles_sample_rate=0.1,      # 10% of traces get a profile
        send_default_pii=False,        # don't ship PII (DB rows, headers, etc.)
        attach_stacktrace=True,
    )

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse
from starlette.exceptions import HTTPException as StarletteHTTPException

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

# Suppress noisy INFO messages that the google-genai SDK emits directly to
# the root logger (no named logger, so we can't filter by logger name).
class _RootFilter(logging.Filter):
    _SUPPRESS = ("AFC is enabled with max remote calls",)
    def filter(self, record: logging.LogRecord) -> bool:
        msg = record.getMessage()
        return not any(s in msg for s in self._SUPPRESS)

logging.getLogger().addFilter(_RootFilter())


async def _pending_job_watcher() -> None:
    """Background loop that recovers stuck Pending jobs. Runs only in API mode.

    Two recovery paths, checked every 60 s:

    1. step=ingest jobs (GPU-owned):
       Re-fires trigger_ingest() for each stuck job so the GPU can claim and
       process it. Handles the case where the initial trigger (fired at job
       creation) failed because the GPU was cold/unavailable.

    2. Other Pending jobs (worker-owned: translate, ingest+summarize, etc.):
       Wakes the worker service via a health ping so it polls and claims them.
    """
    from core.db import conn_ctx
    from pipelines.jobs import _wake_worker
    from services.doc_extractor import trigger_ingest

    while True:
        await asyncio.sleep(60)
        try:
            async with conn_ctx() as conn:
                # GPU-owned: Pending step=ingest jobs
                gpu_cur = await conn.execute(
                    """
                    SELECT "Id", "ReportId", "InputData"->>'file_url'
                    FROM ai_jobs
                    WHERE "Status"  = 'Pending'
                      AND "JobType" = 'Ingestion'
                      AND "InputData"->>'step' = 'ingest'
                    """,
                )
                gpu_rows = await gpu_cur.fetchall()

                # Worker-owned: any other Pending job
                worker_cur = await conn.execute(
                    """
                    SELECT COUNT(*) FROM ai_jobs
                    WHERE "Status"  = 'Pending'
                      AND "JobType" != 'Evaluation'
                      AND NOT (
                            "JobType" = 'Ingestion'
                            AND "InputData"->>'step' = 'ingest'
                      )
                    """,
                )
                worker_row = await worker_cur.fetchone()
                worker_count = int(worker_row[0]) if worker_row else 0

            if gpu_rows:
                logger.info(
                    "[watcher] %d stuck GPU ingest job(s) — re-triggering",
                    len(gpu_rows),
                )
                for job_id, report_id, file_url in gpu_rows:
                    if file_url:
                        asyncio.create_task(
                            trigger_ingest(str(job_id), str(report_id), file_url)
                        )

            if worker_count > 0:
                logger.info(
                    "[watcher] %d pending worker job(s) — waking worker",
                    worker_count,
                )
                await _wake_worker()

        except Exception as exc:
            logger.warning("[watcher] pending-job check failed: %s", exc)


@asynccontextmanager
async def lifespan(app_: FastAPI):
    if settings.worker_mode == "api":
        watcher = asyncio.create_task(_pending_job_watcher())
    else:
        watcher = None
    try:
        yield
    finally:
        if watcher:
            watcher.cancel()


app = FastAPI(title="Taqreerk AI Service", version="1.0.0", lifespan=lifespan)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)


# ── Error handling ──────────────────────────────────────────────────────────
# Goal: every error reaches the client as JSON with a stable shape that the
# frontend / .NET backend can parse. Sentry still captures the full trace
# server-side, so this is purely about what the caller sees.
#
# Response shape:
#   { "error": <human message>,
#     "type":  <error class name>,
#     "request_id": <uuid for support>,
#     "detail": <extra info, only in non-prod> }
#
# 500s never leak internal traces; 4xx pass through HTTPException details.

_IS_PROD = settings.environment == "production"


@app.exception_handler(StarletteHTTPException)
async def http_exception_handler(request: Request, exc: StarletteHTTPException):
    return JSONResponse(
        status_code=exc.status_code,
        content={
            "error": exc.detail if isinstance(exc.detail, str) else "Request failed",
            "type": "HTTPException",
            "request_id": getattr(request.state, "request_id", None),
            "detail": exc.detail if not isinstance(exc.detail, str) else None,
        },
    )


@app.exception_handler(RequestValidationError)
async def validation_exception_handler(request: Request, exc: RequestValidationError):
    return JSONResponse(
        status_code=422,
        content={
            "error": "Invalid request body",
            "type": "ValidationError",
            "request_id": getattr(request.state, "request_id", None),
            "detail": exc.errors(),
        },
    )


@app.exception_handler(Exception)
async def unhandled_exception_handler(request: Request, exc: Exception):
    request_id = getattr(request.state, "request_id", str(uuid.uuid4()))
    logger.exception("Unhandled error [%s] on %s %s", request_id, request.method, request.url.path)
    if settings.sentry_dsn:
        sentry_sdk.capture_exception(exc)

    return JSONResponse(
        status_code=500,
        content={
            "error": "Something went wrong. Please try again or contact support with the request_id.",
            "type": type(exc).__name__,
            "request_id": request_id,
            # In staging we expose the actual message for faster debugging.
            # In production we hide it so we don't leak internal details.
            "detail": str(exc) if not _IS_PROD else None,
        },
    )


@app.middleware("http")
async def add_request_id(request: Request, call_next):
    request.state.request_id = str(uuid.uuid4())
    response = await call_next(request)
    response.headers["X-Request-ID"] = request.state.request_id
    return response


@app.get("/health")
async def health():
    return {"status": "healthy", "mode": settings.worker_mode}


# Routers imported after /health so health check always responds even if imports fail
try:
    from api.chat import router as chat_router
    from api.reports import router as reports_router

    app.include_router(chat_router,    prefix="/api/ai")
    app.include_router(reports_router, prefix="/api/ai")
    logger.info("Routers loaded successfully.")
except Exception as exc:
    logger.error(f"Failed to load routers: {exc}", exc_info=True)
    if settings.sentry_dsn:
        sentry_sdk.capture_exception(exc)


# ── Worker entrypoint ───────────────────────────────────────────────────────
# When WORKER_MODE=worker the container runs `python main.py`, which calls
# main() below. The Dockerfile picks the right CMD based on WORKER_MODE.

def main() -> None:
    """Launch whichever runtime mode is configured.

    Worker mode never returns under normal operation — it loops forever. If it
    crashes hard, Cloud Run restarts the container, which is the desired
    behaviour for a queue worker.

    SIGTERM handling: Cloud Run sends SIGTERM when it wants to replace/stop the
    instance (new revision deployment, scale-to-zero, etc.). We catch it and
    set a shutdown event so the worker loop finishes the current job before
    exiting rather than dying mid-job and leaving the row stuck in 'Processing'.
    Cloud Run waits up to 10 s after SIGTERM before sending SIGKILL, so this
    is best-effort for jobs that complete quickly; long-running jobs are
    protected by the stale-job sweeper on the next worker boot.
    """
    if settings.worker_mode == "worker":
        from pipelines.jobs import run_worker_loop

        logger.info("Starting in WORKER mode")

        loop = asyncio.new_event_loop()
        asyncio.set_event_loop(loop)
        shutdown_event = asyncio.Event()

        def _handle_signal(sig: int, *_) -> None:
            name = signal.Signals(sig).name
            logger.info(
                "Worker received %s — will finish current job then exit", name
            )
            loop.call_soon_threadsafe(shutdown_event.set)

        signal.signal(signal.SIGTERM, _handle_signal)
        signal.signal(signal.SIGINT,  _handle_signal)

        try:
            loop.run_until_complete(run_worker_loop(shutdown_event))
        finally:
            loop.close()
        logger.info("Worker exited cleanly.")
        return

    # API mode — defer to uvicorn launched from the Dockerfile CMD. Running
    # uvicorn programmatically here would double-import main.py during reload,
    # so we just exit cleanly and let the supervisor (Cloud Run) start uvicorn.
    logger.info("WORKER_MODE=api: start uvicorn via the Dockerfile CMD instead.")


if __name__ == "__main__":
    main()
