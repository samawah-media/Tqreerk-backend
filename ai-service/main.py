import logging
import uuid

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

app = FastAPI(title="Taqreerk AI Service", version="1.0.0")

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
    return {"status": "healthy"}


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
