"""Hypothetical-Question Embeddings (HyQE) generator for ingest.

Why this exists
===============
Embedding chunk text alone gives mediocre recall on user questions that
phrase concepts very differently from the chunk's prose — common in
bilingual financial / government reports where a user says
"ما حجم العائدات السنوية؟" and the chunk reads "Total revenue grew 8.2%
to SAR 14B…". Pure-text semantic match misses; HyQE patches it.

For each "real" chunk we ask a fast LLM (gemini-2.5-flash-lite by default):

    "List up to N questions this passage answers, in Arabic and English."

Each generated question is then stored as its OWN row in `report_chunks`
with `embedding = embed(question)` and `parent_chunk_id` pointing to the
real chunk. At retrieval time, when a hypothetical row hits, the SQL
substitutes the parent chunk's content before returning to the agent —
so the agent only ever sees real prose.

Failure model
=============
Generation failures NEVER break ingest. A chunk that fails HyQE simply
doesn't get hypothetical rows; retrieval still works on its real
embedding. We log and continue.
"""
from __future__ import annotations

import asyncio
import json
import logging
from typing import Any

from google.genai import types

from core.config import settings
from services.gemini import _call_with_retry, _log_genai_usage

logger = logging.getLogger(__name__)


# Tight, deterministic prompt. Flash-lite handles bilingual list generation
# reliably; verbose preambles waste tokens and slow ingest.
_PROMPT = """For the passage below, list up to {n} natural-language questions a reader could ask that this passage directly answers.

Rules:
  • Mix Arabic and English questions so cross-language retrieval works.
  • Use NATURAL phrasing a reader would actually type — no formal headings.
  • Each question must be ANSWERABLE from the passage alone, not from
    general knowledge.
  • Skip "what is the title", "summarise this", or other meta-questions.
  • If the passage is structural (a heading, page number, footer) and
    answers nothing useful, return an empty list.

Return JSON: {{"questions": ["...", "..."]}}"""


_SCHEMA = {
    "type": "object",
    "properties": {
        "questions": {
            "type": "array",
            "items": {"type": "string"},
        },
    },
    "required": ["questions"],
}


# ── Public API ──────────────────────────────────────────────────────────────

async def generate_for_chunks(
    chunks: list[str],
    *,
    questions_per_chunk: int | None = None,
) -> list[list[str]]:
    """Return a list-of-lists: questions[i] are the variants for chunks[i].

    Runs each chunk through one Flash call, capped at
    `settings.hyqe_max_concurrency` simultaneous calls. Order is preserved.
    A chunk whose generation fails returns [] — ingest continues without
    HyQE rows for that chunk.
    """
    if not settings.hyqe_enabled or not chunks:
        return [[] for _ in chunks]

    n = questions_per_chunk or settings.hyqe_questions_per_chunk
    if n <= 0:
        return [[] for _ in chunks]

    sem = asyncio.Semaphore(max(1, settings.hyqe_max_concurrency))
    loop = asyncio.get_running_loop()

    async def _one(idx: int, chunk: str) -> list[str]:
        async with sem:
            try:
                return await asyncio.wait_for(
                    loop.run_in_executor(None, _generate_sync, chunk, n),
                    timeout=settings.hyqe_timeout_seconds,
                )
            except asyncio.TimeoutError:
                logger.info("[hyqe] chunk %d timed out — skipping", idx)
                return []
            except Exception as exc:
                logger.warning("[hyqe] chunk %d failed: %s — skipping", idx, exc)
                return []

    return list(await asyncio.gather(*[
        _one(i, c) for i, c in enumerate(chunks)
    ]))


# ── Internals ───────────────────────────────────────────────────────────────

def _generate_sync(chunk_text: str, n: int) -> list[str]:
    """Run one Flash call. _call_with_retry handles SSL/connection errors
    the same way as every other Gemini call in the service."""
    # Trim very long chunks to keep ingest cost bounded — the first 2000 chars
    # of a chunk are plenty for question generation; the chunk text we already
    # cap at 2000 chars (DEFAULT_CHUNK_CHARS) so this is a no-op in practice
    # but defensive against atomic/oversized blocks.
    snippet = chunk_text[:2000].strip()
    if not snippet:
        return []

    prompt = _PROMPT.format(n=n)
    response = _call_with_retry(
        "hyqe_generate",
        lambda client: client.models.generate_content(
            model=settings.hyqe_model,
            contents=[prompt, f"PASSAGE:\n{snippet}"],
            config=types.GenerateContentConfig(
                temperature=0.4,    # tiny variety so questions aren't all alike
                response_mime_type="application/json",
                response_schema=_SCHEMA,
                max_output_tokens=384,
            ),
        ),
    )
    _log_genai_usage(
        response,
        name="hyqe_generate",
        model=settings.hyqe_model,
    )
    parsed = json.loads(response.text or "{}")
    raw: list[Any] = parsed.get("questions") or []

    out: list[str] = []
    for q in raw:
        if not isinstance(q, str):
            continue
        q = q.strip()
        # Drop trivially-short or duplicate items.
        if len(q) < 8:
            continue
        if q in out:
            continue
        out.append(q)
        if len(out) >= n:
            break
    return out
