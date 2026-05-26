"""Content-addressable cache for chunk + query embeddings.

Why this module exists
======================
Every re-ingest of a report (and many ingests of similar reports that share
boilerplate sections) re-embeds chunks against Vertex AI even though the
text + model + spec haven't changed. Vertex bills per character; the cache
turns those repeats into a single SQL round-trip.

Cache key
=========
The PRIMARY KEY is sha256(spec || \\x00 || normalized_text) where
`spec = "{model}|{task_type}|{dim}"`. Changing any of model / task_type /
output dimension produces a different key automatically — no flush step.

Text normalisation is intentionally minimal: NFKC + strip leading/trailing
whitespace. We do NOT lowercase, stem, or de-diacritise: those would
produce false cache hits (different texts that share a vector slot).

Failure model
=============
Cache lookup / write failures NEVER break ingest. On any error we log and
fall through to the live embedder. The cache is a savings layer, not a
correctness boundary.
"""
from __future__ import annotations

import hashlib
import logging
import unicodedata
from typing import Any

import numpy as np
from psycopg import AsyncConnection

logger = logging.getLogger(__name__)


def _spec(model: str, task_type: str, dim: int) -> str:
    return f"{model}|{task_type}|{dim}"


def _normalize(text: str) -> str:
    """Conservative normalisation — NFKC + outer-strip only. Anything more
    aggressive would conflate genuinely different chunks."""
    return unicodedata.normalize("NFKC", text).strip()


def cache_key(model: str, task_type: str, dim: int, text: str) -> str:
    """Deterministic SHA256 hex digest used as the table primary key."""
    h = hashlib.sha256()
    h.update(_spec(model, task_type, dim).encode("utf-8"))
    h.update(b"\x00")  # separator so concatenated forms can't collide
    h.update(_normalize(text).encode("utf-8"))
    return h.hexdigest()


async def lookup_many(
    conn: AsyncConnection,
    *,
    model: str,
    task_type: str,
    dim: int,
    texts: list[str],
) -> dict[int, list[float]]:
    """Look up cached embeddings for a batch.

    Returns a dict {index_in_input_list -> embedding}. Missing indices are
    cache misses the caller must embed via the live API.

    Also bumps `last_used_at` on the rows that hit so an LRU janitor can
    evict cold entries safely.
    """
    if not texts:
        return {}

    keys = [cache_key(model, task_type, dim, t) for t in texts]
    key_to_indices: dict[str, list[int]] = {}
    for i, k in enumerate(keys):
        key_to_indices.setdefault(k, []).append(i)

    try:
        cur = await conn.execute(
            """
            SELECT cache_key, embedding
              FROM chunk_embedding_cache
             WHERE cache_key = ANY(%s)
            """,
            [list(key_to_indices.keys())],
        )
        rows = await cur.fetchall()
    except Exception as exc:
        logger.warning("[embedding_cache] lookup failed: %s", exc)
        return {}

    found: dict[int, list[float]] = {}
    hit_keys: list[str] = []
    for row in rows:
        k = row[0]
        emb = row[1]
        if isinstance(emb, str):
            emb = _parse_pgvector_text(emb)
        else:
            emb = list(emb)
        for i in key_to_indices.get(k, ()):
            found[i] = emb
        hit_keys.append(k)

    if hit_keys:
        try:
            await conn.execute(
                "UPDATE chunk_embedding_cache SET last_used_at = now() WHERE cache_key = ANY(%s)",
                [hit_keys],
            )
        except Exception as exc:
            logger.debug("[embedding_cache] last_used bump failed: %s", exc)

    if found:
        logger.info(
            "[embedding_cache] hit %d/%d (model=%s task=%s)",
            len(found), len(texts), model, task_type,
        )
    return found


async def insert_many(
    conn: AsyncConnection,
    *,
    model: str,
    task_type: str,
    dim: int,
    items: list[tuple[str, list[float]]],
) -> None:
    """Persist newly-computed embeddings. ON CONFLICT DO NOTHING so concurrent
    workers racing on the same text are safe."""
    if not items:
        return
    spec = _spec(model, task_type, dim)
    try:
        for text, vec in items:
            k = cache_key(model, task_type, dim, text)
            preview = text[:200]
            await conn.execute(
                """
                INSERT INTO chunk_embedding_cache
                    (cache_key, spec, embedding, text_preview)
                VALUES (%s, %s, %s, %s)
                ON CONFLICT (cache_key) DO NOTHING
                """,
                [k, spec, np.array(vec, dtype=np.float32), preview],
            )
    except Exception as exc:
        logger.warning("[embedding_cache] insert failed: %s", exc)


def _parse_pgvector_text(s: str) -> list[float]:
    """psycopg returns vector as the literal string '[a,b,c,...]' when no
    pgvector adapter is registered. Convert to a Python list."""
    s = s.strip()
    if s.startswith("[") and s.endswith("]"):
        s = s[1:-1]
    if not s:
        return []
    return [float(x) for x in s.split(",")]
