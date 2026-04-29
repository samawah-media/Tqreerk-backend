"""HTTP entry point — POST /v1/extract.

The route is intentionally thin: it validates the request, decodes the image
bytes, and hands off to the orchestrator. Heavy lifting (model inference)
happens in a thread pool because Florence-2 / EasyOCR / Docling all use
blocking PyTorch / OpenCV calls — running them on the FastAPI event loop
would freeze the /health probe during inference.

Auth model
==========
Cloud Run IAM is the primary layer (--no-allow-unauthenticated; ai-service
SA has roles/run.invoker). The `X-Internal-Token` header is a defence-in-
depth shared secret: when settings.internal_api_token is set, we reject any
request lacking the matching token. Leaving it empty disables that check —
useful for local dev, never set on production unless IAM alone is enough.
"""
from __future__ import annotations

import asyncio
import base64
import logging

from fastapi import APIRouter, Header, HTTPException

import time

from core.config import settings
from models.schema import (
    EmbedRequest,
    EmbedResponse,
    ExtractDocumentRequest,
    ExtractDocumentResponse,
    ExtractRequest,
    ExtractResponse,
)
from pipeline import embeddings
from pipeline.orchestrator import process_document, process_page

logger = logging.getLogger(__name__)

router = APIRouter(prefix="/v1", tags=["extract"])


@router.post("/extract", response_model=ExtractResponse)
async def extract(
    body: ExtractRequest,
    x_internal_token: str | None = Header(default=None),
) -> ExtractResponse:
    """Run layout + tables + figures + formulas + OCR on one PDF page."""
    _check_token(x_internal_token)

    try:
        png_bytes = base64.b64decode(body.image_b64, validate=False)
    except Exception as exc:
        raise HTTPException(status_code=400, detail=f"image_b64 is not valid base64: {exc}")

    if not png_bytes:
        raise HTTPException(status_code=400, detail="image_b64 decoded to empty bytes")

    # Run the full pipeline in a worker thread — every model call below is
    # blocking, so we offload to a thread pool to keep the event loop free
    # for /health and other concurrent requests.
    loop = asyncio.get_running_loop()
    response = await loop.run_in_executor(
        None,
        process_page,
        png_bytes,
        body.page_number,
        body.options,
    )

    logger.info(
        "extract: page=%d blocks=%d tables=%d figures=%d formulas=%d latency=%dms",
        response.page_number,
        len(response.blocks),
        len(response.metadata.tables),
        len(response.metadata.figures),
        len(response.metadata.formulas),
        response.latency_ms,
    )
    return response


@router.post("/extract_document", response_model=ExtractDocumentResponse)
async def extract_document(
    body: ExtractDocumentRequest,
    x_internal_token: str | None = Header(default=None),
) -> ExtractDocumentResponse:
    """Run the pipeline over an entire PDF.

    Preferred path for callers that have the original document bytes —
    avoids rasterising the text layer (Docling reads digital PDFs
    natively), gives reading order across page breaks, and amortises
    Docling's per-call overhead over the whole document.
    """
    _check_token(x_internal_token)

    try:
        pdf_bytes = base64.b64decode(body.pdf_b64, validate=False)
    except Exception as exc:
        raise HTTPException(status_code=400, detail=f"pdf_b64 is not valid base64: {exc}")

    if not pdf_bytes:
        raise HTTPException(status_code=400, detail="pdf_b64 decoded to empty bytes")

    loop = asyncio.get_running_loop()
    response = await loop.run_in_executor(
        None,
        process_document,
        pdf_bytes,
        body.options,
        body.page_range,
    )

    logger.info(
        "extract_document: pages=%d tables=%s figures=%s formulas=%s latency=%dms",
        response.document_metadata.page_count,
        response.document_metadata.has_tables,
        response.document_metadata.has_figures,
        response.document_metadata.has_formulas,
        response.total_latency_ms,
    )
    return response


@router.post("/embed", response_model=EmbedResponse)
async def embed(
    body: EmbedRequest,
    x_internal_token: str | None = Header(default=None),
) -> EmbedResponse:
    """Encode a list of strings into 768-dim embeddings on the GPU.

    Replaces the previous Vertex AI text-embedding-004 call from ai-service.
    Same vector dimension so the existing pgvector(768) column is unchanged.
    """
    _check_token(x_internal_token)

    if not body.texts:
        raise HTTPException(status_code=400, detail="texts must be non-empty")
    if not embeddings.is_ready():
        raise HTTPException(status_code=503, detail="embedding model is still loading")

    started = time.perf_counter()
    loop = asyncio.get_running_loop()
    vectors = await loop.run_in_executor(
        None, embeddings.embed, body.texts, body.kind,
    )
    elapsed_ms = int((time.perf_counter() - started) * 1000)

    if not vectors:
        # is_ready() said yes but encode returned nothing — model crashed
        # mid-flight (OOM, CUDA error). Surface 500 so the caller retries.
        raise HTTPException(status_code=500, detail="embedding encode failed")

    logger.info(
        "embed: kind=%s n=%d latency=%dms",
        body.kind, len(body.texts), elapsed_ms,
    )
    return EmbedResponse(
        embeddings=vectors,
        dim=len(vectors[0]) if vectors else 768,
        model=settings.embed_model_id,
        latency_ms=elapsed_ms,
    )


def _check_token(provided: str | None) -> None:
    """Defence-in-depth shared-secret check. No-op when the token is unset
    so local development doesn't need to plumb headers through every test.
    """
    expected = settings.internal_api_token
    if not expected:
        return
    if provided != expected:
        raise HTTPException(status_code=401, detail="Invalid X-Internal-Token")
