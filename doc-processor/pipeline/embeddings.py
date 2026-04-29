"""sentence-transformers wrapper — text → 768-dim embedding on GPU.

Why this exists
===============
Embeddings used to live in ai-service via Vertex AI's `text-embedding-004`.
Cross-region calls from Cloud Run (me-central1) to Vertex were SSL-EOF'ing at
the GCLB, with no clean way to debug from the application layer. Moving
embeddings onto the existing GPU service collapses two HTTP hops (extract +
embed) into one and removes Vertex from the request path entirely.

Model contract
==============
- 768-dim float vectors (matches `report_chunks.embedding vector(768)`).
- Multilingual — Arabic and English share the same embedding space, so a
  query in either language can retrieve chunks in either.
- E5 family expects task-prefixed inputs:
    "query: <text>"     for retrieval queries
    "passage: <text>"   for stored passages
  We accept a `kind` parameter so callers (ingest vs. chat) pass the right
  prefix without leaking E5-specific knowledge into ai-service.

Failure model
=============
init() flips `_init_attempted` before doing any work; even if the load fails,
is_ready() flips to True and `embed()` returns an empty list. Caller (ai-
service) treats empty as a hard failure since embeddings are non-optional —
this is different from figure captioning, which can degrade silently.
"""
from __future__ import annotations

import logging
from typing import Literal, Optional

import numpy as np
import torch

from core.config import settings

logger = logging.getLogger(__name__)


# ── Module state ────────────────────────────────────────────────────────────

_model: Optional["SentenceTransformer"] = None  # type: ignore[name-defined]
_device: Optional[str] = None
_init_attempted: bool = False


# E5-family prefixes. Other multilingual embedders (BGE, etc.) ignore unknown
# prefixes harmlessly, so we apply them unconditionally for portability.
EmbedKind = Literal["query", "passage"]
_PREFIX = {"query": "query: ", "passage": "passage: "}


def init() -> None:
    """Load the sentence-transformer onto the configured device.

    Idempotent: subsequent calls are no-ops. is_ready() reflects whether the
    load succeeded — a failed load doesn't crash the service, it just leaves
    embed() returning empties.
    """
    global _model, _device, _init_attempted
    if _init_attempted:
        return
    _init_attempted = True

    try:
        from sentence_transformers import SentenceTransformer
    except ImportError as exc:
        logger.exception("embeddings: sentence-transformers not installed: %s", exc)
        return

    _device = settings.device if torch.cuda.is_available() else "cpu"
    logger.info(
        "embeddings: loading model=%s device=%s max_seq=%d",
        settings.embed_model_id, _device, settings.embed_max_seq_length,
    )

    try:
        model = SentenceTransformer(settings.embed_model_id, device=_device)
        model.max_seq_length = settings.embed_max_seq_length
        # Half-precision on GPU keeps quality identical for retrieval but
        # roughly doubles throughput. Keep fp32 on CPU (fp16 there is slower).
        if settings.fp16 and _device == "cuda":
            model = model.half()
        model.eval()
        _model = model
        logger.info("embeddings: ready")
    except Exception as exc:
        logger.exception("embeddings: failed to load model: %s", exc)


def is_ready() -> bool:
    return _init_attempted and _model is not None


# ── Public API ──────────────────────────────────────────────────────────────

@torch.inference_mode()
def embed(texts: list[str], kind: EmbedKind = "passage") -> list[list[float]]:
    """Return one 768-dim vector per input string.

    Args:
        texts: list of UTF-8 strings, possibly empty. Empty strings produce a
               zero vector rather than being skipped, so the output length
               always equals the input length (callers can zip safely).
        kind:  E5 task prefix — "passage" for ingest-time chunks, "query"
               for chat-time questions.

    Returns:
        List of 768-dim Python lists (json-serialisable). Empty list if the
        model failed to load — caller should surface that as an error.
    """
    if not is_ready():
        logger.warning("embeddings: embed() called before init succeeded")
        return []
    if not texts:
        return []

    prefix = _PREFIX.get(kind, "passage: ")
    inputs = [prefix + (t or "") for t in texts]

    try:
        # normalize_embeddings=True gives unit vectors so cosine similarity
        # reduces to a dot product downstream (pgvector <=> operator).
        vectors = _model.encode(  # type: ignore[union-attr]
            inputs,
            batch_size=settings.embed_batch_size,
            convert_to_numpy=True,
            normalize_embeddings=True,
            show_progress_bar=False,
        )
    except torch.cuda.OutOfMemoryError as exc:
        logger.warning("embeddings: OOM at batch=%d: %s", settings.embed_batch_size, exc)
        torch.cuda.empty_cache()
        return []
    except Exception as exc:
        logger.exception("embeddings: encode failed: %s", exc)
        return []

    # encode() returns float32 even when the model is fp16; cast to plain
    # Python lists so the JSON response stays small and standard.
    if isinstance(vectors, np.ndarray):
        return vectors.astype(np.float32).tolist()
    return [list(v) for v in vectors]
