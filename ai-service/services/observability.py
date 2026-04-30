"""Langfuse client wrapper for chat + ingest tracing.

Why this exists
===============
Every chat request and ingest job benefits from a structured trace tree:
spans show per-phase latency, the generation event captures prompt +
completion + tokens for cost tracking, and per-trace `score` events
attach Ragas metrics asynchronously. Sentry is great for errors but can't
answer "why did this answer score 0.4 on faithfulness?" — Langfuse can.

This module:
    • Lazy-initializes a single Langfuse client on first use.
    • Provides no-op fallbacks when Langfuse is disabled / misconfigured —
      production code can call `obs.span(...)` unconditionally.
    • Owns the sample-rate logic so callers don't repeat `random() < rate`.
    • Flushes pending events at process exit (Cloud Run batches up to ~5 s
      of events; without a flush we lose them on instance recycle).

Failure model
=============
A Langfuse outage MUST NEVER break user requests. Every public call here
catches and logs exceptions, returning a no-op span/trace stub. The only
visible symptom of a Langfuse outage is "no traces in the dashboard."
"""
from __future__ import annotations

import atexit
import logging
import random
from contextlib import contextmanager
from typing import Any, Iterator

from core.config import settings

logger = logging.getLogger(__name__)


# ── No-op fallback objects ──────────────────────────────────────────────────
# Returned when Langfuse is disabled or initialization failed. Callers can
# treat them like real Langfuse handles — every method is a safe no-op.

class _NoopHandle:
    """Stand-in for a Langfuse trace / span / generation object."""
    id: str = ""  # so callers reading .id don't crash

    def update(self, **_: Any) -> "_NoopHandle":
        return self

    def end(self, **_: Any) -> "_NoopHandle":
        return self

    def span(self, **_: Any) -> "_NoopHandle":
        return self

    def generation(self, **_: Any) -> "_NoopHandle":
        return self

    def event(self, **_: Any) -> "_NoopHandle":
        return self

    def score(self, **_: Any) -> "_NoopHandle":
        return self


_NOOP = _NoopHandle()


# ── Client singleton ────────────────────────────────────────────────────────

_client: Any = None
_client_attempted: bool = False


def _client_or_none() -> Any:
    """Return the cached Langfuse client, or None when disabled / unreachable.

    First call constructs and caches; subsequent calls are O(1). Construction
    never raises — a misconfigured Langfuse turns into a stream of no-ops."""
    global _client, _client_attempted
    if _client_attempted:
        return _client
    _client_attempted = True

    if not settings.langfuse_enabled:
        logger.info("[obs] langfuse disabled by config")
        return None
    if not (settings.langfuse_host and
            settings.langfuse_public_key and
            settings.langfuse_secret_key):
        logger.info("[obs] langfuse keys/host missing; tracing skipped")
        return None

    try:
        from langfuse import Langfuse  # type: ignore[import-not-found]

        _client = Langfuse(
            host=settings.langfuse_host,
            public_key=settings.langfuse_public_key,
            secret_key=settings.langfuse_secret_key,
            # batched flushing: send events every 5s or when 100 events queue.
            # Fine for chat traffic; ingest traces flush on `update(end=...)`.
            flush_at=100,
            flush_interval=5,
        )
        # Best-effort flush at process exit so we don't lose the last few
        # events when a Cloud Run instance is recycled.
        atexit.register(_safe_flush)
        logger.info("[obs] langfuse client ready host=%s", settings.langfuse_host)
        return _client
    except Exception as exc:
        logger.exception("[obs] langfuse init failed: %s", exc)
        return None


def _safe_flush() -> None:
    if _client is None:
        return
    try:
        _client.flush()
    except Exception:
        pass


def flush() -> None:
    """Explicit flush — used at end of streaming endpoints to make sure the
    chat's trace is committed before the request returns."""
    _safe_flush()


# ── Sample-rate gating ──────────────────────────────────────────────────────

def should_trace() -> bool:
    """Return True when this request should be traced.

    Sampling is per-call independent (no consistent-hash sampling) — at our
    volume this is fine; switch to hash-on-session if we ever need to keep
    a session's whole trace tree together at <100% sample rates."""
    if not settings.langfuse_enabled:
        return False
    rate = settings.langfuse_trace_sample_rate
    if rate >= 1.0:
        return True
    if rate <= 0.0:
        return False
    return random.random() < rate


# ── Tracing entry points ────────────────────────────────────────────────────

def trace(
    *,
    name: str,
    user_id: str | None = None,
    session_id: str | None = None,
    input: Any | None = None,
    metadata: dict | None = None,
    tags: list[str] | None = None,
) -> Any:
    """Start a Langfuse trace. Returns a no-op handle when tracing is off
    or sampling drops the request."""
    if not should_trace():
        return _NOOP
    client = _client_or_none()
    if client is None:
        return _NOOP

    try:
        return client.trace(
            name=name,
            user_id=user_id,
            session_id=session_id,
            input=input,
            metadata=metadata,
            tags=tags,
        )
    except Exception as exc:
        logger.warning("[obs] trace(%s) failed: %s", name, exc)
        return _NOOP


@contextmanager
def span(parent: Any, *, name: str, input: Any | None = None,
         metadata: dict | None = None) -> Iterator[Any]:
    """Context-managed span. Auto-ends on exit; on exception, marks status
    so Langfuse shows the error inline."""
    handle: Any = _NOOP
    try:
        handle = parent.span(name=name, input=input, metadata=metadata)
    except Exception as exc:
        logger.debug("[obs] span(%s) start failed: %s", name, exc)

    try:
        yield handle
    except Exception as exc:
        try:
            handle.update(level="ERROR", status_message=str(exc)[:500])
        except Exception:
            pass
        raise
    finally:
        try:
            handle.end()
        except Exception:
            pass


def generation(
    parent: Any,
    *,
    name: str,
    model: str,
    input: Any,
    output: Any | None = None,
    usage: dict | None = None,
    metadata: dict | None = None,
) -> Any:
    """Record a Langfuse generation event (LLM call). Returns the handle so
    callers can `.update(output=..., usage=...)` once streaming completes."""
    try:
        return parent.generation(
            name=name,
            model=model,
            input=input,
            output=output,
            usage=usage,
            metadata=metadata,
        )
    except Exception as exc:
        logger.debug("[obs] generation(%s) failed: %s", name, exc)
        return _NOOP


def score(
    *,
    trace_id: str,
    name: str,
    value: float,
    comment: str | None = None,
) -> None:
    """Attach a numeric score to an existing trace. Used by the eval worker
    to push Ragas metrics back onto the chat trace asynchronously."""
    if not trace_id:
        return
    client = _client_or_none()
    if client is None:
        return
    try:
        client.score(trace_id=trace_id, name=name, value=value, comment=comment)
    except Exception as exc:
        logger.warning("[obs] score(%s=%s) on trace=%s failed: %s",
                       name, value, trace_id, exc)
