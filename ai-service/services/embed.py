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

import logging
from typing import Literal

from google.genai import types

from core.config import settings
from services.gemini import _call_with_retry

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
        response = _call_with_retry(
            op,
            lambda client, b=batch: client.models.embed_content(
                model=settings.gemini_embed_model,
                contents=b,
                config=config,
            ),
        )
        embeddings = response.embeddings or []
        if len(embeddings) != len(batch):
            raise RuntimeError(
                f"embedding API returned {len(embeddings)} vectors for {len(batch)} inputs"
            )
        results.extend(list(e.values) for e in embeddings)

    return results


def embed_passages(texts: list[str]) -> list[list[float]]:
    """Embed chunk passages for ingest-time storage in report_chunks."""
    return _embed(texts, _TASK_TYPE["passage"])


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
