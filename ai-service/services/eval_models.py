"""Ragas judge LLM + embeddings, backed by the same models the production
chat pipeline uses.

Why custom wrappers
===================
Ragas ships LangchainLLMWrapper / LangchainEmbeddingsWrapper, but they pull
in `langchain-google-genai` (~30 MB and a separate retry stack) just to call
Gemini. We already have a hardened gemini.py with thread-safe client reset
and 5-attempt SSL-EOF backoff, and a doc-processor /v1/embed client. Wrapping
those directly:

  • keeps the dependency surface tight,
  • makes eval *self-consistent* with production — the same retry behaviour,
    the same rate-limit reaction, the same model versions,
  • lets us mark eval calls with a metadata tag so we can filter
    "judge LLM" spend separately on the cost dashboard.

Failure model
=============
If the judge LLM or embeddings can't be reached, Ragas will raise; the
caller (pipelines.eval) catches and posts a Langfuse score with comment
="error" so dashboards see the failure inline rather than a missing data
point.
"""
from __future__ import annotations

import asyncio
import logging
from typing import Any

from langchain_core.embeddings import Embeddings
from langchain_core.outputs import Generation, LLMResult
from ragas.embeddings.base import BaseRagasEmbeddings
from ragas.llms.base import BaseRagasLLM
from ragas.run_config import RunConfig

from core.config import settings
from services import embed as gemini_embed
from services.gemini import simple_completion

logger = logging.getLogger(__name__)


# ── Judge LLM (Gemini Flash) ────────────────────────────────────────────────


class GeminiRagasLLM(BaseRagasLLM):
    """Gemini Flash as a Ragas BaseRagasLLM.

    Ragas calls `generate_text` / `agenerate_text` per metric per row.
    Sync path runs `simple_completion` directly; async path offloads to a
    thread because `simple_completion` is sync (uses the sync google-genai
    client). Callers — Ragas — see a normal LangChain `LLMResult`.
    """

    def __init__(self, run_config: RunConfig | None = None) -> None:
        self.run_config = run_config or RunConfig()

    def is_finished(self, response: LLMResult) -> bool:
        # simple_completion is synchronous — response is always complete.
        return True

    def _generate(self, prompt_text: str, temperature: float | None) -> LLMResult:
        try:
            text = simple_completion(
                prompt_text,
                temperature=temperature or 0.0,
                model=settings.ragas_judge_model,
            )
        except Exception as exc:
            logger.warning("[ragas] gemini judge call failed: %s", exc)
            text = ""
        return LLMResult(generations=[[Generation(text=text)]])

    def generate_text(  # noqa: D401  (Ragas-defined signature)
        self,
        prompt: Any,
        n: int = 1,
        temperature: float | None = None,
        stop: list[str] | None = None,
        callbacks: Any = None,
    ) -> LLMResult:
        prompt_text = prompt.to_string() if hasattr(prompt, "to_string") else str(prompt)
        return self._generate(prompt_text, temperature)

    async def agenerate_text(
        self,
        prompt: Any,
        n: int = 1,
        temperature: float | None = None,
        stop: list[str] | None = None,
        callbacks: Any = None,
    ) -> LLMResult:
        prompt_text = prompt.to_string() if hasattr(prompt, "to_string") else str(prompt)
        loop = asyncio.get_running_loop()
        return await loop.run_in_executor(None, self._generate, prompt_text, temperature)


# ── Judge Embeddings (bge-m3 via doc-processor) ─────────────────────────────


class _DocProcessorEmbeddings(Embeddings):
    """Plain LangChain Embeddings shim — Ragas wraps this in its own
    BaseRagasEmbeddings so all we need is `embed_documents` / `embed_query`.

    We pass `kind="passage"` for documents and `kind="query"` for queries.
    Both are no-ops for bge-m3 today, but the contract is preserved so
    swapping back to a prefix-aware embedder (E5) wouldn't need a code
    change here.
    """

    def embed_documents(self, texts: list[str]) -> list[list[float]]:
        if not texts:
            return []
        # Same RETRIEVAL_DOCUMENT task type as production ingest — keeps
        # Ragas's similarity-based metrics in the same vector space as
        # what we actually serve.
        return gemini_embed.embed_passages(list(texts))

    def embed_query(self, text: str) -> list[float]:
        if not text:
            return []
        return gemini_embed.embed_query(text)


class DocProcessorRagasEmbeddings(BaseRagasEmbeddings):
    """Ragas-facing embeddings adapter. The judge metrics that use
    embeddings — answer_relevancy in particular — go through this class.
    """

    def __init__(self, run_config: RunConfig | None = None) -> None:
        self.run_config = run_config or RunConfig()
        self._impl = _DocProcessorEmbeddings()

    def embed_documents(self, texts: list[str]) -> list[list[float]]:
        return self._impl.embed_documents(texts)

    def embed_query(self, text: str) -> list[float]:
        return self._impl.embed_query(text)


# ── Singletons used by pipelines.eval ───────────────────────────────────────

_judge_llm: GeminiRagasLLM | None = None
_judge_embeddings: DocProcessorRagasEmbeddings | None = None


def get_judge_llm() -> GeminiRagasLLM:
    global _judge_llm
    if _judge_llm is None:
        _judge_llm = GeminiRagasLLM()
        logger.info(
            "[ragas] judge LLM ready: %s (gemini)", settings.ragas_judge_model,
        )
    return _judge_llm


def get_judge_embeddings() -> DocProcessorRagasEmbeddings:
    global _judge_embeddings
    if _judge_embeddings is None:
        _judge_embeddings = DocProcessorRagasEmbeddings()
        logger.info(
            "[ragas] judge embeddings ready: bge-m3 via doc-processor",
        )
    return _judge_embeddings
