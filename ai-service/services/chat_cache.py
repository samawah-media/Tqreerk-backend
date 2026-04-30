"""Two-tier semantic cache for chat answers, backed by Postgres + pgvector.

Why a chat cache
================
Most chat traffic on a given report is heavily duplicated: "summarise this",
"give me page 3", "what is on page 5" repeated by many users. Without a cache,
each one costs:

  • 1 embed call           (~150 ms, paid)
  • 1 hybrid SQL query     (~50 ms)
  • 1 reranker call        (~150 ms, paid)
  • 1 Gemini stream        (~1-3 s, paid)

A cache hit returns the stored answer in a single SQL round-trip — typically
under 10 ms — and skips every paid call.

Two tiers
=========
Layer 1 — exact match
    Key  = sha256(report_id || normalized_question_text)
    Cost = single PRIMARY KEY lookup.
    Catches verbatim repeats including capitalisation / whitespace differences
    after normalisation.

Layer 2 — semantic match
    For the same report, find the most similar cached question via cosine
    distance on the stored question embedding. If similarity ≥ threshold
    (default 0.95), reuse that entry's answer.
    Cost = 1 embed call + 1 small index scan over recent cache rows.
    Only worth it when layer 1 misses; we still skip retrieval + LLM.

Schema (created by EF migration AddChatCache):
    chat_cache(
        cache_key text PK,
        report_id uuid,
        question text,
        question_emb vector(1024),
        answer text,
        source_pages jsonb,
        hit_count int,
        created_at timestamptz,
        expires_at timestamptz
    )

The table is purely Python-managed. EF declares the schema; the .NET API
never reads or writes it.
"""
import hashlib
import json
import logging
import re
import time
from uuid import UUID

import numpy as np
from psycopg import AsyncConnection

from core.config import settings

logger = logging.getLogger(__name__)


# ── Question normalisation ───────────────────────────────────────────────────
# Goal: collapse trivial differences ("Page 5?" vs "page 5") so they share an
# exact-match cache slot. We deliberately keep this conservative — no stemming,
# no stop-word removal — because over-aggressive normalisation creates false
# cache hits across actually-different questions.

_WHITESPACE = re.compile(r"\s+")
_TRAILING_PUNCT = re.compile(r"[\s؟؛.?!,؟؛،]+$")


def _normalize_question(q: str) -> str:
    """Lowercase, strip trailing punctuation/whitespace, collapse runs of WS."""
    q = q.strip().lower()
    q = _TRAILING_PUNCT.sub("", q)
    q = _WHITESPACE.sub(" ", q)
    return q


def _make_cache_key(report_id: UUID, question: str) -> str:
    """SHA256(report_id || normalized_question) hex digest — used as PRIMARY KEY."""
    h = hashlib.sha256()
    h.update(str(report_id).encode("utf-8"))
    h.update(b"\x00")  # field separator so concatenated forms can't collide
    h.update(_normalize_question(question).encode("utf-8"))
    return h.hexdigest()


# ── Public API ───────────────────────────────────────────────────────────────

class CacheHit:
    """A successful cache hit. `tier` distinguishes how the entry was found
    so callers can log / trace cache effectiveness in production."""
    __slots__ = ("answer", "source_pages", "tier")

    def __init__(self, answer: str, source_pages: list[int], tier: str):
        self.answer = answer
        self.source_pages = source_pages
        self.tier = tier  # "exact" or "semantic"


async def lookup(
    conn: AsyncConnection,
    report_id: UUID,
    question: str,
    question_embedding: np.ndarray | None = None,
) -> CacheHit | None:
    """Try exact then semantic lookup. Returns None on miss.

    `question_embedding` is optional: pass it if you already have it (e.g. you
    embedded the question for retrieval anyway), otherwise we skip layer 2.
    Layer 2 is only valuable as a side effect of an embedding the caller is
    already paying for; embedding twice would defeat the cost saving.
    """
    if not settings.chat_cache_enabled:
        return None

    # Layer 1 — exact match on cache_key. Sub-ms lookup, no embedding cost.
    cache_key = _make_cache_key(report_id, question)
    cur = await conn.execute(
        """
        SELECT answer, source_pages
        FROM chat_cache
        WHERE cache_key = %s AND expires_at > now()
        """,
        [cache_key],
    )
    row = await cur.fetchone()
    if row:
        await _bump_hit(conn, cache_key)
        source_pages = _coerce_pages(row[1])
        logger.info("chat_cache exact-hit report=%s", report_id)
        return CacheHit(row[0], source_pages, tier="exact")

    # Layer 2 — semantic match on the same report. Skipped when caller didn't
    # already pay for an embedding, since embedding solely to maybe-hit the
    # cache often costs more than it saves.
    if question_embedding is None:
        return None

    cur = await conn.execute(
        """
        SELECT cache_key, answer, source_pages,
               1 - (question_emb <=> %s) AS similarity
        FROM chat_cache
        WHERE report_id = %s
          AND expires_at > now()
          AND question_emb IS NOT NULL
        ORDER BY question_emb <=> %s
        LIMIT 1
        """,
        [question_embedding, str(report_id), question_embedding],
    )
    row = await cur.fetchone()
    if row and float(row[3]) >= settings.chat_cache_semantic_threshold:
        await _bump_hit(conn, row[0])
        source_pages = _coerce_pages(row[2])
        logger.info(
            "chat_cache semantic-hit report=%s sim=%.3f",
            report_id, float(row[3]),
        )
        return CacheHit(row[1], source_pages, tier="semantic")

    return None


async def store(
    conn: AsyncConnection,
    report_id: UUID,
    question: str,
    answer: str,
    source_pages: list[int],
    question_embedding: np.ndarray | None = None,
) -> None:
    """Persist a Q→A entry. Upserts on cache_key so the same exact question for
    the same report only ever has one row (which gets refreshed)."""
    if not settings.chat_cache_enabled:
        return

    # Don't cache empty / obviously-broken answers — they'd just poison the
    # cache and force users into manual cache invalidation.
    if not answer or not answer.strip():
        return

    cache_key = _make_cache_key(report_id, question)
    expires_at_seconds = settings.chat_cache_exact_ttl_seconds
    # `expires_at` uses NOW() + interval; passing seconds via parameter keeps
    # the value tunable per env without rewriting the SQL.
    await conn.execute(
        """
        INSERT INTO chat_cache
            (cache_key, report_id, question, question_emb, answer,
             source_pages, hit_count, created_at, expires_at)
        VALUES
            (%s, %s, %s, %s, %s, %s::jsonb, 0, now(),
             now() + (%s || ' seconds')::interval)
        ON CONFLICT (cache_key) DO UPDATE SET
            question     = EXCLUDED.question,
            question_emb = EXCLUDED.question_emb,
            answer       = EXCLUDED.answer,
            source_pages = EXCLUDED.source_pages,
            expires_at   = EXCLUDED.expires_at
        """,
        [
            cache_key,
            str(report_id),
            question,
            question_embedding,
            answer,
            json.dumps(source_pages),
            str(expires_at_seconds),
        ],
    )
    await conn.commit()


async def sweep_expired(conn: AsyncConnection) -> int:
    """Delete expired rows. Call from a periodic worker; not on the hot path."""
    cur = await conn.execute("DELETE FROM chat_cache WHERE expires_at <= now()")
    await conn.commit()
    return cur.rowcount or 0


# ── Internal helpers ─────────────────────────────────────────────────────────

async def _bump_hit(conn: AsyncConnection, cache_key: str) -> None:
    """Increment hit counter so we can track which cache entries earn their
    keep. Best-effort: failures are logged but never break the request flow."""
    try:
        await conn.execute(
            "UPDATE chat_cache SET hit_count = hit_count + 1 WHERE cache_key = %s",
            [cache_key],
        )
        await conn.commit()
    except Exception as exc:
        logger.warning("chat_cache hit_count bump failed: %s", exc)


def _coerce_pages(value) -> list[int]:
    """source_pages is jsonb → may come back parsed (list) or as a string."""
    if value is None:
        return []
    if isinstance(value, list):
        return [int(p) for p in value]
    if isinstance(value, str):
        try:
            parsed = json.loads(value)
            return [int(p) for p in parsed] if isinstance(parsed, list) else []
        except (json.JSONDecodeError, ValueError, TypeError):
            return []
    return []


# Exposed for the chat handler so it can include the normalised key in logs
# without depending on hashlib directly.
def make_cache_key(report_id: UUID, question: str) -> str:
    return _make_cache_key(report_id, question)
