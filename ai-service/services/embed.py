"""Vertex / AI Studio embedding client — query + passage embeddings.

Why this module exists
======================
Replaces the doc-processor /v1/embed call (which loaded bge-m3 on the L4
GPU). Moving to a managed embedding API:

  • Eliminates GPU cold-start in the chat path (the old setup burned
    30-90s on first query after the worker scaled to zero).
  • Halves DB storage from vector(1024) → vector(768).
  • Trades a modest Arabic quality drop (~75 → ~62 MIRACL) for permanent
    chat-latency stability — the right call for production UX.

Model = gemini-embedding-001 (settings.gemini_embed_model). Multilingual,
Matryoshka-truncated to 768 dims so the schema stays compact.

Same model embeds both passages (ingest) AND queries (chat) so the vector
spaces are coherent — different `task_type` arguments only nudge the
representation, they don't switch model. Mixing models would silently
break retrieval.

Reuses gemini.py's _call_with_retry path so SSL EOFs and connection resets
are handled identically to chat / vision calls.
"""
from __future__ import annotations

import asyncio
import logging
import time
from typing import Literal

from google.genai import types

from core.config import settings
from core.db import conn_ctx
from services import embedding_cache
from services.gemini import _call_with_retry, _is_quota_error, _log_genai_usage

logger = logging.getLogger(__name__)


# Public API surface — passage vs query are explicit so callers can't pass
# the wrong intent. gemini-embedding-001 task_type values:
#   RETRIEVAL_DOCUMENT — for passages stored in the index
#   RETRIEVAL_QUERY    — for the user's question at chat time
EmbedKind = Literal["passage", "query"]

_TASK_TYPE = {
    "passage": "RETRIEVAL_DOCUMENT",
    "query":   "RETRIEVAL_QUERY",
}


# Vertex AI embedding API hard limit: 250 texts per request.
_BATCH_SIZE = 250

# Vertex enforces embed_content_input_tokens_per_minute as a sliding window —
# _call_with_retry raises quota errors immediately (no fallback model exists
# for embeddings), so retry here with a delay long enough to outlive the
# 60s window.
_QUOTA_RETRY_SECONDS = 65
_QUOTA_MAX_ATTEMPTS = 3


def _embed(texts: list[str], task_type: str) -> list[list[float]]:
    """Batched call to the embedding API. Splits into ≤250-text sub-batches
    to stay within the Vertex AI limit, then concatenates results in order."""
    if not texts:
        return []

    config = types.EmbedContentConfig(
        task_type=task_type,
        output_dimensionality=settings.gemini_embed_dim,
    )
    op = f"embed_{task_type.lower()}"

    results: list[list[float]] = []
    for batch_start in range(0, len(texts), _BATCH_SIZE):
        batch = texts[batch_start : batch_start + _BATCH_SIZE]

        for quota_attempt in range(_QUOTA_MAX_ATTEMPTS):
            try:
                response = _call_with_retry(
                    op,
                    lambda client, b=batch: client.models.embed_content(
                        model=settings.gemini_embed_model,
                        contents=b,
                        config=config,
                    ),
                )
                break
            except Exception as exc:
                if not _is_quota_error(exc) or quota_attempt == _QUOTA_MAX_ATTEMPTS - 1:
                    raise
                logger.warning(
                    "[embed] %s hit quota limit, sleeping %ds before retry "
                    "(quota attempt %d/%d): %s",
                    op, _QUOTA_RETRY_SECONDS, quota_attempt + 1, _QUOTA_MAX_ATTEMPTS, exc,
                )
                time.sleep(_QUOTA_RETRY_SECONDS)

        _log_genai_usage(
            response,
            name=op,
            model=settings.gemini_embed_model,
            metadata={"batch_size": len(batch)},
        )
        embeddings = response.embeddings or []
        if len(embeddings) != len(batch):
            raise RuntimeError(
                f"embedding API returned {len(embeddings)} vectors for {len(batch)} inputs"
            )
        results.extend(list(e.values) for e in embeddings)

    return results


def embed_passages(texts: list[str]) -> list[list[float]]:
    """Embed chunk passages for ingest-time storage in report_chunks.

    Synchronous variant — kept for callers that aren't on the event loop.
    Prefer `embed_passages_async` from async ingest paths so the
    chunk_embedding_cache short-circuits already-seen text.
    """
    return _embed(texts, _TASK_TYPE["passage"])


async def embed_passages_async(texts: list[str]) -> list[list[float]]:
    """Async passage embedding with content-addressable caching.

    Pipeline:
      1. Hash each input text (NFKC + strip; spec = model|task|dim).
      2. SELECT cached embeddings for known hashes (one round-trip).
      3. For misses only, run the live Vertex API in a worker thread.
      4. Write the new embeddings back to chunk_embedding_cache.
      5. Reassemble vectors in the original input order.

    The cache is best-effort: any DB error logs and falls through to a
    full live embed so ingest never breaks because the cache is unhealthy.
    """
    if not texts:
        return []

    model = settings.gemini_embed_model
    task = _TASK_TYPE["passage"]
    dim = settings.gemini_embed_dim

    cached: dict[int, list[float]] = {}
    try:
        async with conn_ctx() as conn:
            cached = await embedding_cache.lookup_many(
                conn, model=model, task_type=task, dim=dim, texts=texts,
            )
            await conn.commit()
    except Exception as exc:
        logger.warning("[embed] cache lookup failed, falling through: %s", exc)

    miss_indices = [i for i in range(len(texts)) if i not in cached]
    miss_texts = [texts[i] for i in miss_indices]

    new_vectors: list[list[float]] = []
    if miss_texts:
        loop = asyncio.get_running_loop()
        new_vectors = await loop.run_in_executor(
            None, _embed, miss_texts, task,
        )
        if len(new_vectors) != len(miss_texts):
            raise RuntimeError(
                f"_embed returned {len(new_vectors)} vectors for "
                f"{len(miss_texts)} miss inputs"
            )
        try:
            async with conn_ctx() as conn:
                await embedding_cache.insert_many(
                    conn,
                    model=model, task_type=task, dim=dim,
                    items=list(zip(miss_texts, new_vectors)),
                )
                await conn.commit()
        except Exception as exc:
            logger.warning("[embed] cache write failed: %s", exc)

    miss_map = dict(zip(miss_indices, new_vectors))
    return [cached[i] if i in cached else miss_map[i] for i in range(len(texts))]


def embed_query(text: str) -> list[float]:
    """Embed a single user question for retrieval. Returns one 768-dim vector
    in the same space as the passage vectors."""
    if not text:
        return []
    vectors = _embed([text], _TASK_TYPE["query"])
    return vectors[0] if vectors else []


def embed_queries(texts: list[str]) -> list[list[float]]:
    """Batch query-embedding variant — used by chat_cache for batched cosine
    lookups, and by find_similar_reports."""
    return _embed(texts, _TASK_TYPE["query"])
