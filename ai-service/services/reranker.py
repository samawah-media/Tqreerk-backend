"""Cross-encoder reranker — now via doc-processor /v1/rerank on GPU.

Why we moved off Vertex AI Ranking
==================================
Vertex Ranking (semantic-ranker-default-004) is decent on Arabic but lags
BAAI/bge-reranker-v2-m3 by a meaningful margin on MIRACL-style benchmarks,
and every chat request was paying a Vertex round-trip on top of the embed
round-trip. The GPU service now hosts both — embed and rerank are
co-located, so a chat query fires off two same-region HTTPS calls instead
of two cross-region Vertex calls.

API surface
===========
ai-service POSTs query + candidate passages to doc-processor /v1/rerank.
The doc-processor returns scored ids in descending relevance order; we
re-key those scores back onto the caller's full candidate dicts (preserving
page_number, content, metadata, etc.) so the rest of the chat pipeline is
unchanged.

Failure mode
============
If /v1/rerank is unreachable or errors, we log the error and fall through
to the original retrieval order — same fail-soft behaviour as before. The
reranker is a quality booster, not a hard dependency.
"""
import logging
import time
from typing import Sequence

import httpx

from core.config import settings

logger = logging.getLogger(__name__)


_http: httpx.Client | None = None


def _client() -> httpx.Client:
    """Module-level httpx client. Reusing the connection pool keeps the
    chat-time call to a single round-trip; cold start the first time."""
    global _http
    if _http is None:
        _http = httpx.Client(timeout=settings.doc_processor_timeout_seconds)
    return _http


def _build_headers() -> dict[str, str]:
    """Auth headers for the doc-processor call.

    Mirrors services.doc_extractor: optional X-Internal-Token shared secret
    plus a Google-signed ID token so Cloud Run IAM can validate the caller
    against `roles/run.invoker` on the doc-processor service.
    """
    headers: dict[str, str] = {"Content-Type": "application/json"}
    if settings.doc_processor_token:
        headers["X-Internal-Token"] = settings.doc_processor_token

    # ID token via metadata server. Lazy-imported so unit tests don't pay
    # the google-auth import cost when the reranker isn't exercised.
    try:
        from google.auth.transport.requests import Request as AuthRequest
        from google.oauth2 import id_token as oauth_id_token

        token = oauth_id_token.fetch_id_token(AuthRequest(), settings.doc_processor_url)
        if token:
            headers["Authorization"] = f"Bearer {token}"
    except Exception as exc:
        logger.debug("reranker: ID token fetch failed (%s); proceeding without", exc)

    return headers


def rerank(
    query: str,
    candidates: Sequence[dict],
    top_k: int | None = None,
) -> list[dict]:
    """Rerank candidate chunks against the query via the GPU service.

    Args:
        query: the user's question.
        candidates: list of {"content": str, ...} dicts. Any extra keys are
            preserved on the returned items so callers can carry page_number,
            metadata, etc. through the rerank.
        top_k: number of items to keep. Defaults to settings.reranker_top_k.

    Returns:
        The candidates re-ordered, truncated to top_k. Each item gains a
        `rerank_score` float in [0, 1] (sigmoid-normalised on the GPU side).
        On API failure, returns `candidates[:top_k]` unmodified.
    """
    if not candidates:
        return []

    keep = top_k or settings.reranker_top_k

    if not settings.reranker_enabled:
        return list(candidates[:keep])

    if not (settings.doc_processor_enabled and settings.doc_processor_url):
        logger.warning(
            "reranker: doc-processor not configured; falling back to retrieval order"
        )
        return list(candidates[:keep])

    url = settings.doc_processor_url.rstrip("/") + "/v1/rerank"
    payload = {
        "query": query,
        "candidates": [
            # Use the original index as the id so we can re-key without
            # round-tripping the full passage payload back. Trim the text
            # before sending — bge-reranker-v2-m3 only sees the first ~512
            # query + 512 passage tokens anyway, so anything past ~8KB is
            # wasted bandwidth.
            {"id": str(i), "text": (c.get("content") or "")[:8000]}
            for i, c in enumerate(candidates)
        ],
        "top_k": keep,
    }

    started = time.perf_counter()
    try:
        response = _client().post(url, headers=_build_headers(), json=payload)
        response.raise_for_status()
        body = response.json()
    except Exception as exc:
        logger.warning(
            "reranker /v1/rerank failed (%s); falling back to retrieval order", exc,
        )
        return list(candidates[:keep])

    elapsed_ms = int((time.perf_counter() - started) * 1000)
    logger.info(
        "doc-processor rerank n=%d kept=%d latency=%dms model=%s",
        len(candidates), len(body.get("results") or []),
        elapsed_ms, body.get("model"),
    )

    # Re-key by `id` (the original index we passed in) and attach the score
    # so downstream observability / debugging can see what the reranker did.
    reranked: list[dict] = []
    for rec in body.get("results") or []:
        try:
            idx = int(rec["id"])
        except (KeyError, ValueError, TypeError):
            continue
        if 0 <= idx < len(candidates):
            item = dict(candidates[idx])
            item["rerank_score"] = float(rec.get("score", 0.0))
            reranked.append(item)
        if len(reranked) >= keep:
            break

    # Defensive: if doc-processor returned fewer items than expected, top up
    # with the remaining unranked candidates so we always return up to `keep`.
    if len(reranked) < keep:
        seen = {int(rec["id"]) for rec in (body.get("results") or [])
                if str(rec.get("id", "")).isdigit()}
        for i, c in enumerate(candidates):
            if i in seen:
                continue
            item = dict(c)
            item.setdefault("rerank_score", 0.0)
            reranked.append(item)
            if len(reranked) >= keep:
                break

    return reranked
