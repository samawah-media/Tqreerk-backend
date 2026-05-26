"""Bilingual query rewriter for the chat retrieval path.

Why this exists
===============
Taqreerk's report corpus is bilingual (Arabic + English) but most users ask
in only one language. A pure dense retriever crosses languages decently
because the embedder is multilingual; BM25 does not — Arabic keyword
queries miss English passages and vice versa. Rewriting the query into the
*other* language before retrieval recovers that recall on the keyword arm.

A second axis: compound questions ("what are revenues AND headcount in
Q4?") retrieve poorly because the embedding sits halfway between two
topics. Decomposing them into two independent sub-queries lifts top-k for
each topic individually.

Output
======
Always includes the original query, plus up to N variants. The caller
embeds and retrieves for each variant in parallel and unions the
candidates before reranking. Reranker is the truth-teller, so duplicates
or weak variants get filtered there — the rewriter is allowed to be
slightly noisy.

Failure model
=============
Any error (timeout, JSON parse failure, model outage) → fall back to
[original_query]. Chat MUST NEVER fail because the rewriter is unhealthy.
"""
from __future__ import annotations

import asyncio
import json
import logging
import re
from typing import Any

from google.genai import types

from core.config import settings
from services.gemini import _call_with_retry, _log_genai_usage

logger = logging.getLogger(__name__)


# ── Prompt ──────────────────────────────────────────────────────────────────
# Tight, deterministic, JSON-schema-constrained. The system prompt is short
# on purpose — Gemini Flash follows simple instructions reliably; verbose
# preambles waste tokens and add latency.

_REWRITE_PROMPT = """You rewrite a user's search question for a bilingual (Arabic + English) report retrieval system.

Goals:
  1. If the question is in Arabic, produce ONE faithful English translation.
  2. If the question is in English, produce ONE faithful Arabic translation.
  3. If the question contains multiple independent sub-questions, produce up to
     ONE additional variant that isolates the most important sub-question.

Rules:
  • Translations must preserve domain terms (KPIs, organisation names, country
    names) without paraphrasing.
  • Do NOT explain. Do NOT add quotes. Just the rewritten queries.
  • Maximum {max_variants} variants total. Fewer is fine.
  • If the question is already minimal and single-language is sufficient,
    return an empty list.

Return JSON: {{"variants": ["...", "..."]}}"""


_REWRITE_SCHEMA = {
    "type": "object",
    "properties": {
        "variants": {
            "type": "array",
            "items": {"type": "string"},
        },
    },
    "required": ["variants"],
}


# ── Public API ──────────────────────────────────────────────────────────────

async def rewrite_query(query: str) -> list[str]:
    """Return [original, *variants]. Always includes the original first.

    On any failure path (disabled / timeout / parse error) returns just
    [original] so callers can treat the result uniformly.
    """
    if not query or not query.strip():
        return [query]
    if not settings.query_rewriter_enabled:
        return [query]

    max_variants = max(0, int(settings.query_rewriter_max_variants))
    if max_variants <= 0:
        return [query]

    try:
        loop = asyncio.get_running_loop()
        variants = await asyncio.wait_for(
            loop.run_in_executor(None, _rewrite_sync, query, max_variants),
            timeout=settings.query_rewriter_timeout_seconds,
        )
    except asyncio.TimeoutError:
        logger.info("[query_rewriter] timed out after %.1fs — using original only",
                    settings.query_rewriter_timeout_seconds)
        return [query]
    except Exception as exc:
        logger.warning("[query_rewriter] failed: %s — using original only", exc)
        return [query]

    # Dedupe (case-insensitive, whitespace-collapsed) so a no-op rewrite
    # doesn't double the retrieval work.
    seen: set[str] = set()
    out: list[str] = []
    for q in [query, *variants]:
        norm = re.sub(r"\s+", " ", q.strip().lower())
        if not norm or norm in seen:
            continue
        seen.add(norm)
        out.append(q.strip())
    return out


# ── Internals ───────────────────────────────────────────────────────────────

def _rewrite_sync(query: str, max_variants: int) -> list[str]:
    """Synchronous body — runs in a worker thread. Uses _call_with_retry so
    SSL EOFs / connection resets get the same handling as every other Gemini
    call."""
    prompt = _REWRITE_PROMPT.format(max_variants=max_variants)
    contents = [
        prompt,
        f"Question: {query}",
    ]
    response = _call_with_retry(
        "rewrite_query",
        lambda client: client.models.generate_content(
            model=settings.query_rewriter_model,
            contents=contents,
            config=types.GenerateContentConfig(
                temperature=0.0,
                response_mime_type="application/json",
                response_schema=_REWRITE_SCHEMA,
                max_output_tokens=256,
            ),
        ),
    )
    _log_genai_usage(
        response,
        name="rewrite_query",
        model=settings.query_rewriter_model,
    )
    parsed = json.loads(response.text or "{}")
    raw = parsed.get("variants") or []
    out: list[str] = []
    for v in raw:
        if not isinstance(v, str):
            continue
        v = v.strip()
        if v:
            out.append(v)
        if len(out) >= max_variants:
            break
    return out
