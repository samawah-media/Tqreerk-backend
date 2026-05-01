"""FastAPI entrypoint for the doc-processor GPU service.

Lifecycle
=========
1. Container boots → CUDA initialises (if device=cuda).
2. Startup hook eagerly loads all four models so the first real request
   doesn't pay model-load latency stacked on top of cold-start.
3. /health returns 200 only once every model reports ready, so Cloud Run's
   start-up probe doesn't route traffic mid-warmup.
4. Routine requests hit /v1/extract; every model call runs inside a thread
   pool to keep the event loop free for /health and graceful shutdown.

Failure model
=============
A model that fails to load is logged and the process keeps running with
that capability disabled. /health reflects which models are healthy. The
orchestrator's per-stage try/except keeps individual region failures from
propagating up to the request handler.
"""
import logging
import uuid

import sentry_sdk

from core.config import settings

if settings.sentry_dsn:
    sentry_sdk.init(
        dsn=settings.sentry_dsn,
        environment=settings.environment,
        traces_sample_rate=0.1,
        profiles_sample_rate=0.1,
        send_default_pii=False,
        attach_stacktrace=True,
    )

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse
from starlette.exceptions import HTTPException as StarletteHTTPException

from api.extract import router as extract_router
from models.schema import HealthResponse
from pipeline import embeddings, figures, formulas, layout, ocr, reranker

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)


app = FastAPI(title="Taqreerk Doc Processor", version="1.0.0")
app.include_router(extract_router)


# ── Lifecycle ───────────────────────────────────────────────────────────────

@app.on_event("startup")
async def _warm_models() -> None:
    """Load every model up-front so the first real request is fast.

    Each init() runs synchronously in this hook so we know the readiness
    state by the time /health is asked. A failed model logs and the service
    starts in degraded mode — the orchestrator's per-stage error handling
    keeps the rest of the pipeline working.
    """
    for name, fn in [
        ("docling",     layout.init),
        ("easyocr",     ocr.init),
        ("pix2tex",     formulas.init),
        ("florence-2",  figures.init),
        # bge-m3 and bge-reranker-v2-m3 dropped 2026-04-30 — embedding /
        # reranking moved to managed APIs (Vertex). The /v1/embed and
        # /v1/rerank endpoints stay deployed but their model weights are
        # no longer pre-cached in the image, so loading would require a
        # network download at cold start. Skip the warmup; is_ready()
        # returns False; the endpoints respond 503 if anyone calls them.
        # ("embeddings",  embeddings.init),
        # ("reranker",    reranker.init),
    ]:
        try:
            fn()
        except Exception as exc:
            logger.exception("warmup: %s failed to initialise: %s", name, exc)
            if settings.sentry_dsn:
                sentry_sdk.capture_exception(exc)


# ── Health probe ────────────────────────────────────────────────────────────

@app.get("/health", response_model=HealthResponse)
async def health() -> HealthResponse:
    """Per-model readiness probe.

    Cloud Run's startup probe should consume this and only mark the instance
    ready when `status == "healthy"`. We deliberately return 200 in all
    states so an automated probe sees the structured payload, not an HTTP
    error — operators inspect models_loaded to debug a stuck instance.

    Readiness scope (2026-04-30): the doc-processor is responsible only for
    the EXTRACT pipeline (docling, easyocr, pix2tex, florence-2). bge-m3
    and bge-reranker-v2-m3 were deliberately dropped from the warmup path
    when chat moved to Vertex APIs — they're no longer part of the
    "service is ready to serve" definition. We still surface `embeddings`
    and `reranker` in `models_loaded` for ops visibility (so operators can
    see at a glance that the legacy endpoints are intentionally cold), but
    they don't gate the overall status — otherwise this instance would
    report "loading" forever and Cloud Run would never mark it ready.
    """
    import torch

    # Models the extract pipeline actually depends on. /health gates on
    # exactly these — anything else is observability-only.
    required = {
        "docling":    layout.is_ready(),
        "easyocr":    ocr.is_ready(),
        "pix2tex":    formulas.is_ready(),
        "florence-2": figures.is_ready(),
    }

    # Optional models we still expose (legacy /v1/embed and /v1/rerank
    # endpoints respond 503 if anyone calls them). NOT included in the
    # status calculation.
    optional = {
        "embeddings": embeddings.is_ready(),
        "reranker":   reranker.is_ready(),
    }

    if all(required.values()):
        status = "healthy"
    elif any(required.values()):
        status = "loading"
    else:
        status = "unhealthy"

    return HealthResponse(
        status=status,
        models_loaded={**required, **optional},
        gpu_available=torch.cuda.is_available(),
    )


# ── Error handling ──────────────────────────────────────────────────────────

@app.middleware("http")
async def add_request_id(request: Request, call_next):
    request.state.request_id = str(uuid.uuid4())
    response = await call_next(request)
    response.headers["X-Request-ID"] = request.state.request_id
    return response


@app.exception_handler(StarletteHTTPException)
async def http_exception_handler(request: Request, exc: StarletteHTTPException):
    return JSONResponse(
        status_code=exc.status_code,
        content={
            "error": exc.detail if isinstance(exc.detail, str) else "Request failed",
            "type": "HTTPException",
            "request_id": getattr(request.state, "request_id", None),
        },
    )


@app.exception_handler(Exception)
async def unhandled_exception_handler(request: Request, exc: Exception):
    request_id = getattr(request.state, "request_id", str(uuid.uuid4()))
    logger.exception(
        "Unhandled error [%s] on %s %s", request_id, request.method, request.url.path,
    )
    if settings.sentry_dsn:
        sentry_sdk.capture_exception(exc)

    is_prod = settings.environment == "production"
    return JSONResponse(
        status_code=500,
        content={
            "error": "Internal error in doc-processor; falling back is recommended.",
            "type": type(exc).__name__,
            "request_id": request_id,
            "detail": str(exc) if not is_prod else None,
        },
    )
