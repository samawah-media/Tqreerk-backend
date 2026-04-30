"""Online RAG evaluation — runs Ragas metrics on a finished chat and posts
the scores back onto its Langfuse trace.

Why online (vs offline batch)
=============================
Two reasons:

1. **Fresh, real-traffic signal.** Offline eval requires a curated test
   set; we don't have one yet. Scoring live traces gives us a continuously
   updated quality view without any annotation effort.

2. **Tied to the trace.** Each Langfuse trace already has the question,
   retrieved contexts, and answer attached. Scoring it puts the metric on
   the same row a human reviews — they can click into the chat, see what
   was retrieved, and inspect why a faithfulness score dropped.

Architecture
============
The chat handler enqueues an `Evaluation` row in `ai_jobs` after the SSE
stream finishes. The existing worker dispatch routes that row to
`run_eval_job`, which calls into this module:

  pipelines.eval.run_eval(question, contexts, answer, trace_id)
      ├── ragas.evaluate(...)  ← runs on Gemini Flash + bge-m3
      └── for each metric: obs.score(trace_id, name=..., value=...)

Failure model
=============
Eval is best-effort and isolated from chat. A crash here:

  • does NOT fail the chat (the chat already returned to the user),
  • does NOT block the next job (worker catches and marks the eval job
    Failed; no retries, since metrics are advisory not load-bearing),
  • DOES post a Langfuse score with comment="error" so the dashboard
    shows we tried.

Metrics
=======
Six reference-free metrics — picked because they don't need a gold answer:

  1. faithfulness                — does the answer derive from the contexts?
  2. answer_relevancy            — does the answer actually address the question?
  3. context_precision_no_ref    — are retrieved chunks on-topic?
  4. context_recall_no_ref       — are all the chunks needed for the answer present?
  5. noise_sensitivity_relevant   — robustness to noise on relevant chunks
  6. noise_sensitivity_irrelevant — robustness to noise on irrelevant chunks

We rerun the full set per chat. Volume is low enough (max ~20 chats / hour
in staging) that the LLM-judge cost is bounded; sample rate gating lives
upstream in the chat handler.
"""
from __future__ import annotations

import asyncio
import logging
import time
from typing import Any

from datasets import Dataset

from core.config import settings
from services import observability as obs
from services.eval_models import get_judge_embeddings, get_judge_llm

logger = logging.getLogger(__name__)


# Names match Langfuse score names — keep them stable so dashboard filters
# don't break on a Ragas version bump that renames internal class names.
_METRIC_NAMES = {
    "faithfulness":                  "faithfulness",
    "answer_relevancy":              "answer_relevancy",
    "context_precision_without_reference": "context_precision",
    "context_recall_without_reference":    "context_recall",
    "noise_sensitivity_relevant":    "noise_sensitivity_relevant",
    "noise_sensitivity_irrelevant":  "noise_sensitivity_irrelevant",
}


def _build_metric_objects() -> list:
    """Construct the Ragas metric instances at call-time so a failed import
    of one optional metric (Ragas API churn between versions) doesn't break
    the rest. Each metric is built independently and skipped on import error.
    """
    metrics: list = []

    try:
        from ragas.metrics import faithfulness  # type: ignore
        metrics.append(faithfulness)
    except Exception as exc:  # pragma: no cover
        logger.warning("[eval] faithfulness import failed: %s", exc)

    try:
        from ragas.metrics import answer_relevancy  # type: ignore
        metrics.append(answer_relevancy)
    except Exception as exc:
        logger.warning("[eval] answer_relevancy import failed: %s", exc)

    # Reference-free precision/recall live behind class names that have
    # rotated across Ragas versions; try the modern path then fall back.
    try:
        from ragas.metrics import LLMContextPrecisionWithoutReference  # type: ignore
        metrics.append(LLMContextPrecisionWithoutReference())
    except Exception as exc:
        logger.warning(
            "[eval] LLMContextPrecisionWithoutReference unavailable: %s", exc,
        )

    try:
        from ragas.metrics import LLMContextRecall  # type: ignore
        metrics.append(LLMContextRecall())
    except Exception as exc:
        logger.warning("[eval] LLMContextRecall unavailable: %s", exc)

    try:
        from ragas.metrics import NoiseSensitivity  # type: ignore
        # Ragas exposes one class with a `mode` argument in 0.2.x.
        metrics.append(NoiseSensitivity(mode="relevant"))
        metrics.append(NoiseSensitivity(mode="irrelevant"))
    except Exception as exc:
        logger.warning("[eval] NoiseSensitivity unavailable: %s", exc)

    return metrics


def _normalize_metric_name(raw: str) -> str:
    """Map Ragas's per-row column header to our stable score name. Falls
    back to the raw name when we don't have a translation — keeps Langfuse
    receiving SOMETHING rather than dropping the score."""
    return _METRIC_NAMES.get(raw, raw)


async def run_eval(
    question: str,
    contexts: list[str],
    answer: str,
    trace_id: str | None,
    *,
    metric_timeout_seconds: float | None = None,
) -> dict[str, float]:
    """Score a single chat row against the configured metric set and post
    each score onto the matching Langfuse trace.

    Returns the metric → score dict so callers can log it. The dict is
    empty if eval was skipped (disabled / Ragas unavailable). On per-metric
    failure the entry is set to a NaN-like sentinel and a Langfuse score
    is still posted with comment='error' so dashboards see the attempt.
    """
    if not settings.eval_enabled:
        logger.info("[eval] disabled by config; skipping")
        return {}
    if not (question and contexts and answer):
        logger.info("[eval] skipping — missing question/contexts/answer")
        return {}

    timeout = metric_timeout_seconds or settings.eval_metric_timeout_seconds
    metrics = _build_metric_objects()
    if not metrics:
        logger.warning("[eval] no metrics available; aborting")
        return {}

    # Build a one-row dataset. Ragas evaluates a Dataset, not a single dict.
    # `contexts` is a list-of-list-of-strings — one list per row.
    ds = Dataset.from_dict({
        "question": [question],
        "contexts": [list(contexts)],
        "answer":   [answer],
        # Some Ragas metrics insist on a `response` column instead of
        # `answer` in newer versions; we provide both to be future-proof.
        "response": [answer],
        # `user_input` / `retrieved_contexts` aliases for the same reason.
        "user_input":         [question],
        "retrieved_contexts": [list(contexts)],
    })

    judge_llm = get_judge_llm()
    judge_emb = get_judge_embeddings()

    started = time.perf_counter()
    try:
        # Ragas's evaluate() is sync internally; we run it in a thread so
        # the worker's event loop stays responsive (so other jobs can be
        # claimed while a long eval is in flight).
        from ragas import evaluate  # type: ignore

        loop = asyncio.get_running_loop()
        result = await asyncio.wait_for(
            loop.run_in_executor(
                None,
                lambda: evaluate(
                    ds,
                    metrics=metrics,
                    llm=judge_llm,
                    embeddings=judge_emb,
                    raise_exceptions=False,  # individual metric errors → NaN
                    show_progress=False,
                ),
            ),
            timeout=timeout * len(metrics),
        )
    except Exception as exc:
        logger.exception("[eval] ragas.evaluate failed: %s", exc)
        if trace_id:
            obs.score(trace_id=trace_id, name="eval_error",
                      value=0.0, comment=str(exc)[:500])
        return {}

    elapsed_ms = int((time.perf_counter() - started) * 1000)

    # `result` is a `EvaluationResult` (dict-like). The first row carries
    # one float per metric; some Ragas versions return `nan` on per-metric
    # failure rather than raising. Translate nan → None so downstream
    # consumers (Langfuse) get a sensible signal.
    scores: dict[str, float] = {}
    try:
        # Ragas 0.2 `EvaluationResult` exposes `.to_pandas()` — but to avoid
        # pulling pandas just for one row we read via dict-like access.
        for raw_name, value in result.items():  # type: ignore[attr-defined]
            normalized = _normalize_metric_name(raw_name)
            try:
                fval = float(value[0]) if hasattr(value, "__getitem__") else float(value)
            except (TypeError, ValueError):
                fval = float("nan")
            scores[normalized] = fval
    except Exception:
        # Some versions return pandas DataFrame directly.
        try:
            df = result.to_pandas()  # type: ignore[attr-defined]
            for col in df.columns:
                if col in {"question", "contexts", "answer",
                           "response", "user_input", "retrieved_contexts"}:
                    continue
                fval = float(df[col].iloc[0])
                scores[_normalize_metric_name(col)] = fval
        except Exception as exc:
            logger.exception("[eval] could not extract scores: %s", exc)
            return {}

    logger.info(
        "[eval] trace=%s elapsed=%dms scores=%s",
        trace_id, elapsed_ms, scores,
    )

    # Post each score onto the Langfuse trace. NaN → record with
    # comment='nan' so dashboards see the failed metric explicitly.
    if trace_id:
        for name, value in scores.items():
            if value != value:  # NaN check
                obs.score(trace_id=trace_id, name=name, value=0.0,
                          comment="nan")
            else:
                obs.score(trace_id=trace_id, name=name, value=value)
        obs.flush()

    return scores
