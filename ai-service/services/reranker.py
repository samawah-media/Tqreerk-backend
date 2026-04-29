"""Cross-encoder reranker via Vertex AI Ranking API (Discovery Engine).

Why a reranker
==============
Hybrid retrieval (dense + BM25 with RRF) is a great recall stage — it pulls 20
candidate chunks back from Postgres in a few ms. But it ranks every candidate
in isolation: the embedding model never saw the question and the chunk
together. A cross-encoder reranker scores (question, chunk) pairs jointly,
which is far more accurate at telling apart "almost relevant" from "actually
the answer." It typically lifts top-1 accuracy by 10-20 points on RAG eval.

Why Vertex AI Ranking
=====================
  • Native to GCP — uses the same ADC as Gemini and GCS, no new vendor key.
  • Multilingual (incl. Arabic) — purpose-built for cross-lingual scoring.
  • ~150 ms latency for top-20.
  • Pay-per-call: ~$1 per 1k requests, far cheaper than a Gemini round-trip.

API surface
===========
google.cloud.discoveryengine_v1.RankService → .rank(request).

Each request takes a query string and a list of records (id + title + content).
The API returns the same records with an extra `score` field, sorted in
descending relevance order. We pass the chunk index as the record id so the
caller can re-key results back to the original list cheaply.

Failure mode
============
If Vertex Ranking is unreachable or returns an error, we log the error and
fall through to the original ordering. The reranker is a quality booster, not
a hard dependency — degrading to "no rerank" is preferable to failing the chat
request entirely.
"""
import logging
from typing import Sequence

from core.config import settings

logger = logging.getLogger(__name__)

_client = None
_ranking_config: str | None = None


def _init():
    """Lazy import + client construction. Avoids loading the Discovery Engine
    SDK on cold start when the reranker is disabled."""
    global _client, _ranking_config
    if _client is not None:
        return

    # Imported lazily so containers that disable the reranker don't pay the
    # ~200 ms import-time cost of the Discovery Engine client.
    from google.cloud import discoveryengine_v1

    _client = discoveryengine_v1.RankServiceClient()
    # The serving config name follows a fixed format with the "default" ranking
    # location ("global"). Project-scoped, no separate Discovery Engine app.
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
            preserved on the returned items so callers can carry page_number,
            metadata, etc. through the rerank.
        top_k: number of items to keep. Defaults to settings.reranker_top_k.

    Returns:
        The candidates re-ordered, truncated to top_k. Each item gains a
        `rerank_score` float (Vertex returns 0..1, higher = more relevant).
        On API failure, returns `candidates[:top_k]` unmodified.
    """
    if not candidates:
        return []

    keep = top_k or settings.reranker_top_k

    if not settings.reranker_enabled:
        return list(candidates[:keep])

    try:
        _init()
        from google.cloud import discoveryengine_v1

        # Vertex limits each request to <= 200 records, well above our typical
        # 20-candidate pool, so we don't bother batching.
        records = [
            discoveryengine_v1.RankingRecord(
                id=str(i),
                content=(c.get("content") or "")[:8000],  # SDK rejects >8 KB
            )
            for i, c in enumerate(candidates)
        ]

        request = discoveryengine_v1.RankRequest(
            ranking_config=_ranking_config,
            model=settings.reranker_model,
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

    # The API returns records sorted by descending score. Re-key by `id`
    # (the original index we passed in) to recover the full candidate dict
    # and attach the score for downstream observability / debugging.
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

    # Defensive: if Vertex returned fewer items than expected, top up with the
    # remaining unranked candidates so we always return up to `keep` items.
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
