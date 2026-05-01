"""Vertex gemini-embedding-001 client — used by /v1/ingest_full so the
doc-processor can produce ready-to-persist chunk vectors in one round-trip.

Why Vertex (vs the in-house bge-m3 on the GPU)
==============================================
The whole reason embedding was moved off-GPU on 2026-04-30 was the cold-
start tax (30-90s on first call after scale-to-zero). Re-introducing it
here would defeat the point. Calling Vertex from the doc-processor adds
~150 ms RTT cross-region but no cold start and no extra GPU memory.

Same model + dim as ai-service/services/embed.py so newly written
embeddings live in the same vector space as existing report_chunks rows
— no DB migration needed.

Failure model
=============
Embedding errors propagate up to the /v1/ingest_full handler. The handler
catches and returns 502 so the ai-service caller can decide whether to
fall back or fail the job. There's no in-house retry here (ai-service
already retries the outer call with its own backoff).
"""
from __future__ import annotations

import logging
import threading
import time

from google import genai
from google.genai import types

from core.config import settings

logger = logging.getLogger(__name__)


_BATCH_SIZE = 250  # Vertex AI embedding API hard cap (1..251 exclusive).

_client: genai.Client | None = None
_client_lock = threading.Lock()


def _build_client() -> genai.Client:
    """Construct a Vertex genai client with a 120s per-request timeout.

    Without the timeout, a half-open TCP connection can hang the request
    indefinitely — same failure mode that bit ai-service before its 120s
    fix.
    """
    http_opts = {"timeout": 120}
    logger.info(
        "[embedder] building Vertex client project=%s location=%s model=%s",
        settings.gcp_project_id, settings.vertex_location, settings.embed_vertex_model,
    )
    return genai.Client(
        vertexai=True,
        project=settings.gcp_project_id,
        location=settings.vertex_location,
        http_options=http_opts,
    )


def _get_client() -> genai.Client:
    global _client
    with _client_lock:
        if _client is None:
            _client = _build_client()
        return _client


def _replace_client(stale: genai.Client) -> None:
    """Swap the cached client if it's still the one the caller used.

    Keeps concurrent callers from racing on a None window."""
    global _client
    with _client_lock:
        if _client is stale:
            _client = _build_client()


_CONN_ERROR_MARKERS = (
    "ssl", "eof", "connection reset", "broken pipe",
    "remote end closed", "connection aborted", "max retries",
    "timed out", "timeout",
)


def _is_connection_error(exc: Exception) -> bool:
    err = str(exc).lower()
    return any(m in err for m in _CONN_ERROR_MARKERS)


def embed_passages(texts: list[str]) -> list[list[float]]:
    """Embed a list of chunk passages using gemini-embedding-001.

    Splits into ≤250-text batches (Vertex API limit) and calls each batch
    sequentially. Returns one float vector per input in input order.

    Resilient to SSL EOFs and connection resets via a small retry loop —
    each failed batch retries up to 3x with exponential backoff and a
    fresh client.
    """
    if not texts:
        return []

    config = types.EmbedContentConfig(
        task_type="RETRIEVAL_DOCUMENT",
        output_dimensionality=settings.embed_vertex_dim,
    )

    results: list[list[float]] = []
    for batch_start in range(0, len(texts), _BATCH_SIZE):
        batch = texts[batch_start : batch_start + _BATCH_SIZE]
        results.extend(_embed_one_batch(batch, config))
    return results


def _embed_one_batch(
    batch: list[str], config: types.EmbedContentConfig,
) -> list[list[float]]:
    """Call Vertex embed_content for one batch with retry."""
    last_exc: Exception | None = None
    started = time.perf_counter()
    for attempt in range(3):
        client = _get_client()
        try:
            response = client.models.embed_content(
                model=settings.embed_vertex_model,
                contents=batch,
                config=config,
            )
            embeddings = response.embeddings or []
            if len(embeddings) != len(batch):
                raise RuntimeError(
                    f"Vertex returned {len(embeddings)} vectors for {len(batch)} inputs"
                )
            elapsed_ms = int((time.perf_counter() - started) * 1000)
            logger.info(
                "[embedder] batch n=%d latency=%dms attempt=%d",
                len(batch), elapsed_ms, attempt + 1,
            )
            return [list(e.values) for e in embeddings]
        except Exception as exc:
            last_exc = exc
            if _is_connection_error(exc):
                logger.warning(
                    "[embedder] batch attempt %d/%d failed (connection): %s",
                    attempt + 1, 3, exc,
                )
                _replace_client(client)
            else:
                logger.warning(
                    "[embedder] batch attempt %d/%d failed: %s",
                    attempt + 1, 3, exc,
                )
            if attempt < 2:
                time.sleep(2 ** attempt)
    assert last_exc is not None
    raise last_exc
