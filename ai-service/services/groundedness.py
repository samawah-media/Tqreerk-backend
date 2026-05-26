"""Inline groundedness check — one Gemini Flash call after the answer streams.

Why this exists
===============
Ragas faithfulness already runs asynchronously per chat (pipelines/eval.py),
but its score lands minutes later in Langfuse — long after the user has
read (and possibly trusted) a hallucinated answer. This module runs a
much lighter check inline:

  • One Flash call right after the stream finishes
  • Returns a 0..1 score + the list of unsupported claims
  • Caller emits an SSE `warning` event when the score is below
    `settings.groundedness_warn_threshold`
  • Logged to Sentry so quality drift is visible on the existing dashboard

This adds ~600 ms AFTER the user has already seen the response, so it
never blocks first-token latency. If the call itself fails (timeout,
parse error, API outage) we silently fall through — the user has their
answer, we just don't get a faithfulness signal for this turn.
"""
from __future__ import annotations

import asyncio
import json
import logging

from google.genai import types

from core.config import settings
from services.gemini import _call_with_retry, _log_genai_usage

logger = logging.getLogger(__name__)


# Flash follows tight schemas reliably; verbose preambles waste tokens. The
# numeric scale is 0.0..1.0 so we can compare against thresholds without
# parsing prose.
_PROMPT = """You are a strict groundedness judge. Score whether the ANSWER's factual claims are supported by the CONTEXT chunks the assistant retrieved.

Rules:
  • Score 1.0 = every claim is directly supported by the context.
  • Score 0.5 = mixed — some claims supported, others not in the context.
  • Score 0.0 = none of the claims appear in the context (full hallucination).
  • Editorial framing ("Here is a summary…") is not a factual claim — ignore it.
  • A claim cited as [p.X] but actually absent from the corresponding chunk is unsupported.

Return JSON: {"score": 0..1, "unsupported": ["claim1", "claim2"]}
List up to 3 unsupported claims; an empty list when score >= 0.9.
"""


_SCHEMA = {
    "type": "object",
    "properties": {
        "score":       {"type": "number"},
        "unsupported": {"type": "array", "items": {"type": "string"}},
    },
    "required": ["score"],
}


class GroundednessResult:
    __slots__ = ("score", "unsupported")

    def __init__(self, score: float, unsupported: list[str]):
        self.score = score
        self.unsupported = unsupported

    @property
    def is_warning(self) -> bool:
        return self.score < settings.groundedness_warn_threshold


async def check(answer: str, contexts: list[str]) -> GroundednessResult | None:
    """Score `answer` for groundedness against the joined `contexts`.

    Returns None on disabled / empty inputs / any failure path so callers
    can use it as `result = await check(...); if result and result.is_warning: …`
    without nested error handling.
    """
    if not settings.groundedness_check_enabled:
        return None
    if not answer or not answer.strip() or not contexts:
        return None

    # Clip context to a fixed budget — Flash handles ~32 KB of input cheaply,
    # but a chatty turn could feed in 50+ KB of chunks. Cap at ~8 chunks of
    # 4 KB each.
    joined_ctx = "\n\n---\n\n".join(c[:4000] for c in contexts[:8])

    try:
        loop = asyncio.get_running_loop()
        return await asyncio.wait_for(
            loop.run_in_executor(None, _score_sync, answer, joined_ctx),
            timeout=settings.groundedness_check_timeout_seconds,
        )
    except asyncio.TimeoutError:
        logger.info(
            "[groundedness] timed out after %.1fs — skipping",
            settings.groundedness_check_timeout_seconds,
        )
        return None
    except Exception as exc:
        logger.warning("[groundedness] failed: %s — skipping", exc)
        return None


def _score_sync(answer: str, joined_ctx: str) -> GroundednessResult:
    """Synchronous body — runs in a worker thread."""
    contents = [
        _PROMPT,
        f"CONTEXT:\n{joined_ctx}",
        f"ANSWER:\n{answer}",
    ]
    response = _call_with_retry(
        "groundedness_check",
        lambda client: client.models.generate_content(
            model=settings.groundedness_check_model,
            contents=contents,
            config=types.GenerateContentConfig(
                temperature=0.0,
                response_mime_type="application/json",
                response_schema=_SCHEMA,
                max_output_tokens=512,
            ),
        ),
    )
    _log_genai_usage(
        response,
        name="groundedness_check",
        model=settings.groundedness_check_model,
    )
    parsed = json.loads(response.text or "{}")
    raw_score = float(parsed.get("score") or 0.0)
    score = max(0.0, min(1.0, raw_score))
    unsupported = [str(c) for c in (parsed.get("unsupported") or [])][:3]
    return GroundednessResult(score=score, unsupported=unsupported)
