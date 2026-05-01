"""Cross-encoder reranker via Vertex AI Ranking API (Discovery Engine).

Why a reranker
==============
Hybrid retrieval (dense + BM25 with RRF) is a great recall stage — it pulls
~20 candidate chunks back from Postgres in single-digit ms. But it ranks
every candidate in isolation: the embedding model never saw the question
and the chunk together. A cross-encoder reranker scores (question, chunk)
pairs jointly, lifting top-1 accuracy by 10-20 points on RAG eval.

Why Vertex Ranking (vs the bge reranker on GPU)
================================================
We had bge-reranker-v2-m3 on the doc-processor GPU briefly. Moved off it
together with bge-m3 embedding to eliminate the chat-time GPU cold start.
Trade: ~10-15% lower Arabic quality, no 30-90s cold starts. Right call for
production UX.

  • Native to GCP — uses ADC, no new vendor key
  • Multilingual incl. Arabic
  • ~150ms latency for top-20 candidates
  • Pay-per-call: ~$1 per 1k requests

Failure mode
============
On any error (Vertex unreachable, malformed records, timeout), we log and
fall through to the retrieval order. The reranker is a quality booster,
not a hard dependency — degrading to "no rerank" is preferable to failing
the chat request entirely.
"""
from __future__ import annotations

import logging
from typing import Sequence

from core.config import settings

logger = logging.getLogger(__name__)


_client = None
_ranking_config: str | None = None


def _init() -> None:
    """Lazy import + client construction. Avoids loading the Discovery Engine
    SDK at cold start when reranking is disabled."""
    global _client, _ranking_config
    if _client is not None:
        return

    # Imported lazily — saves ~200ms at cold start when the reranker
    # isn't exercised.
    from google.cloud import discoveryengine_v1

    _client = discoveryengine_v1.RankServiceClient()
    # Project-scoped, "global" ranking location is the documented constant.
    _ranking_config = (
        f"projects/{settings.gcp_project_id}/locations/global/"
        f"rankingConfigs/default_ranking_config"
    )


def rerank(
    query: str,
    candidates: Sequence[dict],
    top_k: int | None = None,
) -> list[dict]:
    """Rerank candidate chunks against the query.

    Args:
        query: the user's question.
        candidates: list of {"content": str, ...} dicts. Any extra keys are
            preserved on the returned items so callers can carry page_number
            / metadata / chunk_id through the rerank step.
        top_k: how many to keep. Defaults to settings.reranker_top_k.

    Returns:
        Re-ordered candidates truncated to top_k. Each item gains a
        `rerank_score` float (Vertex 0..1, higher = more relevant). On API
        failure, returns `candidates[:top_k]` in retrieval order with
        rerank_score=0.0.
    """
    if not candidates:
        return []

    keep = top_k or settings.reranker_top_k

    if not settings.reranker_enabled:
        return list(candidates[:keep])

    try:
        _init()
        from google.cloud import discoveryengine_v1

        # Vertex caps each request at <=200 records, well above our 20-pool.
        # Trim each content to 8KB — the SDK rejects larger and the reranker
        # only sees the first ~chunk anyway.
        records = [
            discoveryengine_v1.RankingRecord(
                id=str(i),
                content=(c.get("content") or "")[:8000],
            )
            for i, c in enumerate(candidates)
        ]

        request = discoveryengine_v1.RankRequest(
            ranking_config=_ranking_config,
            model=settings.reranker_vertex_model,
            top_n=keep,
            query=query,
            records=records,
            ignore_record_details_in_response=True,  # we only need id + score
        )
        response = _client.rank(request=request)
    except Exception as exc:
        # Reranker is optional — fall back to retrieval order so the chat
        # request still succeeds. Caller's logs will show the degraded mode.
        logger.warning("reranker failed (%s); falling back to retrieval order", exc)
        return list(candidates[:keep])

    # Vertex returns records sorted by descending score. Re-key by `id`
    # (the original index we passed in) to recover the full candidate dict
    # and attach the score for downstream observability.
    reranked: list[dict] = []
    for rec in response.records:
        try:
            idx = int(rec.id)
        except (ValueError, TypeError):
            continue
        if 0 <= idx < len(candidates):
            item = dict(candidates[idx])
            item["rerank_score"] = float(rec.score)
            reranked.append(item)
        if len(reranked) >= keep:
            break

    # Defensive: if Vertex returned fewer items than expected, top up with
    # remaining unranked candidates so callers always get up to `keep`.
    if len(reranked) < keep:
        seen = {int(rec.id) for rec in response.records if str(rec.id).isdigit()}
        for i, c in enumerate(candidates):
            if i in seen:
                continue
            item = dict(c)
            item.setdefault("rerank_score", 0.0)
            reranked.append(item)
            if len(reranked) >= keep:
                break

    return reranked
