"""HTTP entry point — POST /v1/extract.

The route is intentionally thin: it validates the request, decodes the image
bytes, and hands off to the orchestrator. Heavy lifting (model inference)
happens in a thread pool because Florence-2 / Surya / Docling all use
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
    ExtractOptions,
    ExtractRequest,
    ExtractResponse,
    IngestChunk,
    IngestFullRequest,
    IngestFullResponse,
    IngestFullStats,
    IngestJobAccepted,
    IngestJobRequest,
    IngestRequest,
    IngestResponse,
    RerankRequest,
    RerankResponse,
    RerankResult,
)
from pipeline import chunker, embeddings, reranker, vertex_embedder
from pipeline.ingest import (
    claim_job,
    mark_job_completed,
    mark_job_failed,
    run_ingest,
)
from pipeline.orchestrator import process_document, process_page

logger = logging.getLogger(__name__)

router = APIRouter(prefix="/v1", tags=["extract"])


# NOTE: the per-pod GPU semaphore was dropped 2026-05-08 when /v1/ingest_job
# became synchronous. Cloud Run --concurrency=1 now serialises HTTP requests
# at the LB tier, which is sufficient (request handler holds the GPU for the
# full duration). If --concurrency is ever raised >1, restore the semaphore.


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


@router.post("/rerank", response_model=RerankResponse)
async def rerank(
    body: RerankRequest,
    x_internal_token: str | None = Header(default=None),
) -> RerankResponse:
    """Score (query, passage) pairs with the cross-encoder and return the
    top-k by descending score.

    Replaces the previous Vertex AI Ranking call from ai-service. Same
    fail-soft contract — if the model isn't ready or scoring errors, the
    caller will see 503 and can fall back to retrieval order."""
    _check_token(x_internal_token)

    if not body.candidates:
        raise HTTPException(status_code=400, detail="candidates must be non-empty")
    if not body.query.strip():
        raise HTTPException(status_code=400, detail="query must be non-empty")
    if not reranker.is_ready():
        raise HTTPException(status_code=503, detail="reranker model is still loading")

    started = time.perf_counter()
    loop = asyncio.get_running_loop()
    passages = [c.text for c in body.candidates]
    ranked = await loop.run_in_executor(
        None, reranker.rerank, body.query, passages, body.top_k,
    )
    elapsed_ms = int((time.perf_counter() - started) * 1000)

    if not ranked:
        # is_ready() said yes but predict returned nothing — model crashed
        # mid-flight (OOM, CUDA error). Surface 500 so the caller's fall-soft
        # logic returns the retrieval order unchanged.
        raise HTTPException(status_code=500, detail="reranker scoring failed")

    results = [
        RerankResult(id=body.candidates[idx].id, score=score, rank=rank)
        for rank, (idx, score) in enumerate(ranked)
    ]
    logger.info(
        "rerank: n=%d kept=%d top_score=%.3f latency=%dms",
        len(body.candidates), len(results),
        results[0].score if results else 0.0, elapsed_ms,
    )
    return RerankResponse(
        results=results,
        model=settings.rerank_model_id,
        latency_ms=elapsed_ms,
    )


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


@router.post("/ingest_full", response_model=IngestFullResponse)
async def ingest_full(
    body: IngestFullRequest,
    x_internal_token: str | None = Header(default=None),
) -> IngestFullResponse:
    """Extract → chunk → embed in one round-trip.

    Why this endpoint exists
    ========================
    The legacy flow (/v1/extract_document → ai-service chunks + embeds)
    transferred ~100 MB of structured blocks across regions per ingest. That
    response was big enough to OOM the ai-service worker during JSON parse.
    /v1/ingest_full does extraction here (already in memory), chunks
    server-side, and calls Vertex embed_content from this container — then
    returns just `{chunks, embeddings, metadata}` (~2-3 MB for 134 pages).

    Failure modes
    =============
    - PDF too small / no pages → returns empty chunks, stats reflect 0
    - Chunk cap exceeded       → 413 (caller falls back to per-page mode)
    - Vertex embed fails       → 502 (caller's job runner marks Failed)
    - Extraction fails         → exception bubbles up, FastAPI returns 500
    """
    _check_token(x_internal_token)

    try:
        pdf_bytes = base64.b64decode(body.pdf_b64, validate=False)
    except Exception as exc:
        raise HTTPException(status_code=400, detail=f"pdf_b64 is not valid base64: {exc}")
    if not pdf_bytes:
        raise HTTPException(status_code=400, detail="pdf_b64 decoded to empty bytes")

    overall_started = time.perf_counter()
    loop = asyncio.get_running_loop()

    # Stage 1 — extract. Reuses the same orchestrator as /v1/extract_document
    # so chunk-time output is byte-identical regardless of which endpoint
    # ran. The IngestFullOptions field set is a strict subset of
    # ExtractOptions, so a direct splat works.
    extract_started = time.perf_counter()
    extract_options = ExtractOptions(
        extract_tables=body.options.extract_tables,
        extract_figures=body.options.extract_figures,
        extract_formulas=body.options.extract_formulas,
        ocr_fallback=body.options.ocr_fallback,
        figure_captioning=body.options.figure_captioning,
    )
    extract_response = await loop.run_in_executor(
        None,
        process_document,
        pdf_bytes,
        extract_options,
        None,  # page_range — None = all pages
    )
    extract_latency_ms = int((time.perf_counter() - extract_started) * 1000)

    # Stage 2 — chunk every page locally. Same algorithm as the ai-service
    # implementation it replaces. We build (page_number, chunk_index, ...)
    # tuples up front so we can hard-cap the total before paying the
    # embedding round-trip.
    #
    # Document-level: flatten all pages into one list with a globally-
    # monotonic reading_order and call the chunker once. Then re-scope
    # chunk_index per (primary) page so the response payload still maps
    # cleanly to the report_chunks (ReportId, PageNumber, ChunkIndex) shape.
    # See pipeline/chunker.py for the heading-merge rule and bbox-citation
    # contract.
    chunk_started = time.perf_counter()
    pending: list[dict] = []

    flat_blocks: list[dict] = []
    text_fallback_pages: list[tuple[int, str]] = []
    global_order = 0
    for page in extract_response.pages:
        page_num = page.page_number
        if not page_num:
            continue
        if page.blocks:
            for b in page.blocks:
                flat_blocks.append({
                    "type":          b.type,
                    "content":       b.content,
                    "reading_order": global_order,
                    "page_number":   page_num,
                    "bbox":          b.bbox.model_dump() if b.bbox else None,
                })
                global_order += 1
        elif (page.content or "").strip():
            text_fallback_pages.append((page_num, page.content))

    per_page_counters: dict[int, int] = {}
    if flat_blocks:
        for ch in chunker.chunk_blocks_with_meta(flat_blocks):
            pg = int(ch.get("page_number") or 1)
            idx = per_page_counters.get(pg, 0)
            per_page_counters[pg] = idx + 1
            pending.append({
                "page_number":   pg,
                "chunk_index":   idx,
                "content":       ch["content"],
                "section_title": ch["section_title"],
                "block_types":   ch["block_types"],
                "bboxes":        ch.get("bboxes") or [],
            })

    # Pages without structured blocks (image-only / OCR fallback) still need
    # chunking — they just can't pack across pages, so per-page splitting is
    # the only option.
    for page_num, content in text_fallback_pages:
        base_idx = per_page_counters.get(page_num, 0)
        pieces = chunker.chunk_text(content)
        for offset, piece in enumerate(pieces):
            pending.append({
                "page_number":   page_num,
                "chunk_index":   base_idx + offset,
                "content":       piece,
                "section_title": "",
                "block_types":   ["text"],
                "bboxes":        [],
            })
        per_page_counters[page_num] = base_idx + len(pieces)
    chunk_latency_ms = int((time.perf_counter() - chunk_started) * 1000)

    # Hard cap. Above this, the response payload starts approaching memory
    # pressure for the caller's worker even in the new design — we'd rather
    # the ai-service fall back to per-page mode than ship a 50 MB response.
    if len(pending) > settings.ingest_full_max_chunks:
        raise HTTPException(
            status_code=413,
            detail=(
                f"ingest_full produced {len(pending)} chunks; cap is "
                f"{settings.ingest_full_max_chunks}. Caller should fall "
                "back to per-page extraction for this PDF."
            ),
        )

    # Stage 3 — embed via Vertex. Skipped when the caller asked for chunks
    # only (debugging / dry-run). Runs in a thread because the genai SDK
    # uses a sync httpx client. Local name is `chunk_vectors` so it doesn't
    # shadow the imported `embeddings` module used by the legacy /v1/embed
    # endpoint.
    embed_latency_ms = 0
    chunk_vectors: list[list[float]] = []
    if body.options.embed_chunks and pending:
        embed_started = time.perf_counter()
        texts = [p["content"] for p in pending]
        try:
            chunk_vectors = await loop.run_in_executor(
                None, vertex_embedder.embed_passages, texts,
            )
        except Exception as exc:
            logger.exception("ingest_full: Vertex embed failed: %s", exc)
            raise HTTPException(
                status_code=502,
                detail=f"Vertex embed_content failed: {exc}",
            )
        embed_latency_ms = int((time.perf_counter() - embed_started) * 1000)
        if len(chunk_vectors) != len(pending):
            raise HTTPException(
                status_code=502,
                detail=(
                    f"Vertex returned {len(chunk_vectors)} vectors for "
                    f"{len(pending)} chunks"
                ),
            )

    # Stage 4 — assemble the response.
    chunks_out: list[IngestChunk] = []
    for i, p in enumerate(pending):
        chunks_out.append(IngestChunk(
            page_number=   p["page_number"],
            chunk_index=   p["chunk_index"],
            content=       p["content"],
            section_title= p["section_title"],
            block_types=   p["block_types"],
            bboxes=        p.get("bboxes") or [],
            embedding=     chunk_vectors[i] if chunk_vectors else [],
        ))

    total_latency_ms = int((time.perf_counter() - overall_started) * 1000)
    pages_with_chunks = len({p["page_number"] for p in pending})

    logger.info(
        "ingest_full: pages=%d chunks=%d extract_ms=%d chunk_ms=%d "
        "embed_ms=%d total_ms=%d embed_chunks=%s",
        extract_response.document_metadata.page_count,
        len(chunks_out),
        extract_latency_ms,
        chunk_latency_ms,
        embed_latency_ms,
        total_latency_ms,
        body.options.embed_chunks,
    )

    return IngestFullResponse(
        document_metadata=extract_response.document_metadata,
        chunks=chunks_out,
        extractor=extract_response.extractor,
        embed_model=settings.embed_vertex_model if body.options.embed_chunks else "",
        embed_dim=settings.embed_vertex_dim if body.options.embed_chunks else 0,
        stats=IngestFullStats(
            extract_latency_ms=extract_latency_ms,
            chunk_latency_ms=chunk_latency_ms,
            embed_latency_ms=embed_latency_ms,
            total_latency_ms=total_latency_ms,
            pages_processed=pages_with_chunks,
            chunks_emitted=len(chunks_out),
        ),
    )


@router.post("/ingest", response_model=IngestResponse)
async def ingest(
    body: IngestRequest,
    x_internal_token: str | None = Header(default=None),
) -> IngestResponse:
    """Download → extract → chunk → embed → persist in one GPU-side call.

    The ai-service worker sends { report_id, file_url } and gets back ingest
    stats. No PDF bytes or chunk vectors are transferred back — the doc-
    processor writes directly to report_chunks via the shared Postgres DB.

    Failure modes
    =============
    - DATABASE_URL not set → 500 (misconfiguration; will surface on startup)
    - Download fails       → 502 propagated from httpx / GCS
    - Vertex embed fails   → 502 (retried up to 3x inside vertex_embedder)
    - DB write fails       → 500 (transaction is rolled back automatically)
    """
    _check_token(x_internal_token)

    if not settings.database_url:
        raise HTTPException(
            status_code=500,
            detail="DATABASE_URL is not configured on this doc-processor instance.",
        )

    loop = asyncio.get_running_loop()
    try:
        result = await loop.run_in_executor(
            None, run_ingest, body.report_id, body.file_url, body.options,
        )
    except Exception as exc:
        logger.exception("ingest: pipeline failed for report=%s: %s", body.report_id, exc)
        raise HTTPException(status_code=500, detail=str(exc))

    return IngestResponse(**result)


@router.post("/ingest_job", response_model=IngestJobAccepted)
async def ingest_job(
    body: IngestJobRequest,
    x_internal_token: str | None = Header(default=None),
) -> IngestJobAccepted:
    """Synchronous ingest: GPU owns the full job lifecycle and the HTTP
    request stays open for the entire duration.

    Why synchronous (changed 2026-05-08): with --concurrency=1 on Cloud Run,
    only synchronous handlers cause the autoscaler to spawn another pod.
    The previous fire-and-forget BackgroundTasks pattern returned 202 in
    ~1 s, so 10 simultaneous triggers all landed on the same pod and
    competed for the GPU. Holding the request until run_ingest returns
    makes Cloud Run see the pod as at-capacity, route the next trigger
    to a fresh pod (up to --max-instances), and we get true horizontal
    parallelism scaled by the doc-processor's instance count.

    Pipeline:
      1. Claim the ai_jobs row (Pending → Processing). Skip if another
         trigger beat us to it (returns accepted=False).
      2. Run download → extract → chunk → embed → persist.
      3. Mark Completed or Failed in ai_jobs.
      4. Return 200 with the result.

    The caller (ai-service trigger) sees a slow HTTP response — that's
    intentional. Cloud Run --timeout must be set generously (≥ longest
    expected ingest); 1 hour is the recommended deploy setting.

    The ai-service worker never claims Ingestion rows (filtered out by
    JobType in its claim query) so they only run here.
    """
    _check_token(x_internal_token)

    if not settings.database_url:
        raise HTTPException(
            status_code=500,
            detail="DATABASE_URL is not configured on this doc-processor instance.",
        )

    loop = asyncio.get_running_loop()

    # ── Claim ──────────────────────────────────────────────────────────────
    try:
        claimed = await loop.run_in_executor(None, claim_job, body.job_id)
    except Exception as exc:
        logger.error(
            "[ingest_job] job=%s claim_job raised %s: %s — marking Failed",
            body.job_id, type(exc).__name__, exc,
        )
        try:
            await loop.run_in_executor(
                None, mark_job_failed, body.job_id, f"claim failed: {exc}",
            )
        except Exception:
            pass
        raise HTTPException(status_code=500, detail=f"claim failed: {exc}")

    if not claimed:
        logger.warning(
            "[ingest_job] job=%s already claimed or not Pending — skipping",
            body.job_id,
        )
        # 200 not 409, since "already done" is not an error from the
        # caller's perspective — they fire triggers idempotently and the
        # _pending_job_watcher retries.
        return IngestJobAccepted(accepted=False, job_id=body.job_id)

    logger.info(
        "[ingest_job] job=%s claimed, starting pipeline for report=%s",
        body.job_id, body.report_id,
    )

    # ── Run ────────────────────────────────────────────────────────────────
    try:
        result = await loop.run_in_executor(
            None, run_ingest, body.report_id, body.file_url, body.options, body.job_id,
        )
        await loop.run_in_executor(None, mark_job_completed, body.job_id, result)
        logger.info(
            "[ingest_job] job=%s completed: %d chunks / %d pages",
            body.job_id, result.get("chunks_inserted", 0), result.get("pages_processed", 0),
        )
        return IngestJobAccepted(accepted=True, job_id=body.job_id)
    except Exception as exc:
        logger.exception("[ingest_job] job=%s pipeline failed: %s", body.job_id, exc)
        try:
            await loop.run_in_executor(None, mark_job_failed, body.job_id, str(exc))
        except Exception as mark_exc:
            logger.error(
                "[ingest_job] job=%s also failed to mark Failed: %s",
                body.job_id, mark_exc,
            )
        raise HTTPException(status_code=500, detail=str(exc))


def _check_token(provided: str | None) -> None:
    """Defence-in-depth shared-secret check. No-op when the token is unset
    so local development doesn't need to plumb headers through every test.
    """
    expected = settings.internal_api_token
    if not expected:
        return
    if provided != expected:
        raise HTTPException(status_code=401, detail="Invalid X-Internal-Token")
