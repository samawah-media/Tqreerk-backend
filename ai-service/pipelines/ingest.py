"""PDF ingestion pipeline (chunk-aware, dual-extractor).

Flow
====
  1. Download the PDF from GCS (gs://) or HTTPS.
  2. Try the **full-PDF** extraction path first
     (services.doc_extractor.extract_document). Doc-processor sees the
     original text layer — no rasterisation loss for digital PDFs — and
     resolves reading order across page breaks. Returns one
     {page_number, content, metadata} dict per page, or None to signal
     "skip me" (disabled, oversized, or transient failure).
  3. If full-PDF mode skipped, fall back to **per-page rendering**
     (PyMuPDF → PNG) and call extract_page() on each. extract_page() itself
     falls back to Gemini Vision when doc-processor is unreachable.
  4. Either path yields the same per-page list. Each page's content gets
     chunked (~500-token sub-page chunks via LangChain) and each chunk
     gets its own Gemini embedding.
  5. All chunk rows are written to report_chunks in a single transaction
     after deleting any pre-existing chunks for the report (re-ingest is
     idempotent).

Why this layering: doc-processor is the new, more accurate extractor; Gemini
Vision is the proven baseline. The ingest pipeline never assumes either
works — any single layer failing degrades cleanly to the next, so a misbehaving
GPU service can't block ingest. Which path ran for each chunk is recorded
in `metadata.extractor` for downstream A/B comparison.
"""
import asyncio
import json
import logging
from uuid import UUID

import fitz  # PyMuPDF
import httpx
import numpy as np
from google.cloud import storage

from core.chunking import chunk_text
from core.db import conn_ctx
from services import doc_extractor

logger = logging.getLogger(__name__)

DPI = 150
MAT = fitz.Matrix(DPI / 72, DPI / 72)

# Max concurrent pages being processed at once in the per-page fallback path.
# In full-PDF mode this constant is unused (doc-processor handles internal
# parallelism). Vertex AI Gemini Flash default RPM is generous; 5 parallel
# pages keeps us well under quota even when a page fans out into ~5-6 chunk
# embedding calls.
PARALLELISM = 5


_gcs_client = None


def _gcs():
    global _gcs_client
    if _gcs_client is None:
        _gcs_client = storage.Client()
    return _gcs_client


def _download_from_gcs(gs_uri: str) -> bytes:
    """Download bytes from a gs://bucket/path URI using ADC."""
    assert gs_uri.startswith("gs://")
    bucket_name, _, blob_path = gs_uri[5:].partition("/")
    blob = _gcs().bucket(bucket_name).blob(blob_path)
    return blob.download_as_bytes()


# ── Public entry points ─────────────────────────────────────────────────────

async def ingest_report(
    report_id: UUID, file_url: str, extractor: str = "auto",
) -> dict:
    """Download a PDF (gs:// or https://) and populate report_chunks.

    `extractor` selects the extraction path for A/B comparison:
      - "auto"          (default): doc-processor full-PDF, fall back to per-page
                                   (which itself falls back to Gemini Vision).
      - "doc-processor": force doc-processor only; raise if it returns nothing.
      - "gemini-vision": skip doc-processor entirely, render every page and
                         use Gemini Vision only.

    Returns:
        {
          "pages_processed":  int,  # pages that produced at least one chunk
          "chunks_inserted":  int,  # total chunk rows written
          "pages_total":      int,  # pages in the source PDF (incl. blanks)
          "extractor":        str,  # which path produced the content
        }
    """
    logger.info("[ingest %s] downloading %s", report_id, file_url)
    if file_url.startswith("gs://"):
        pdf_bytes = _download_from_gcs(file_url)
    else:
        async with httpx.AsyncClient(timeout=120) as client:
            resp = await client.get(file_url)
            resp.raise_for_status()
            pdf_bytes = resp.content
    logger.info(
        "[ingest %s] downloaded %.1f MB",
        report_id, len(pdf_bytes) / (1024 * 1024),
    )

    return await ingest_report_bytes(report_id, pdf_bytes, extractor=extractor)


async def ingest_report_bytes(
    report_id: UUID, pdf_bytes: bytes, extractor: str = "auto",
) -> dict:
    # Count pages up-front so the response always reports `pages_total`,
    # even if extraction returns nothing.
    with fitz.open(stream=pdf_bytes, filetype="pdf") as doc:
        pages_total = len(doc)

    logger.info(
        "[ingest %s] PDF has %d pages, extractor=%s",
        report_id, pages_total, extractor,
    )

    # Branch by extractor toggle.
    page_results: list[dict] | None = None

    if extractor == "gemini-vision":
        # Force per-page rendering with Gemini Vision only — bypass doc-processor.
        page_results = await _extract_per_page(pdf_bytes, force_gemini=True)
    elif extractor == "doc-processor":
        # Force the GPU pipeline; no Vision fallback. If it returns nothing,
        # the job fails so the comparison run is unambiguous.
        page_results = await _extract_full_pdf(pdf_bytes)
        if page_results is None:
            raise RuntimeError(
                "extractor='doc-processor' was requested but doc-processor "
                "returned nothing (disabled, oversized, or errored)"
            )
        logger.info(
            "[ingest %s] doc-processor returned %d pages (forced)",
            report_id, len(page_results),
        )
    else:
        # "auto": current default — try doc-processor first, fall back per-page.
        logger.info("[ingest %s] auto: trying full-PDF extractor", report_id)
        page_results = await _extract_full_pdf(pdf_bytes)
        if page_results is None:
            logger.info("[ingest %s] full-PDF unavailable, falling back to per-page", report_id)
            page_results = await _extract_per_page(pdf_bytes)
        else:
            logger.info("[ingest %s] full-PDF returned %d pages", report_id, len(page_results))

    if not page_results:
        logger.warning(
            "[ingest %s] no pages were extracted (%d total in source PDF)",
            report_id, pages_total,
        )

    # Step 3 — chunk every page, then embed all chunks in one GPU batch via
    # doc-processor /v1/embed. Single round-trip replaces N Vertex calls and
    # keeps inference inside the same Cloud Run network as extraction.
    logger.info("[ingest %s] chunk+embed phase starting (%d pages)", report_id, len(page_results))
    chunk_rows = await _chunk_and_embed(page_results)
    logger.info("[ingest %s] chunk+embed produced %d rows", report_id, len(chunk_rows))

    # Step 4 — write to report_chunks atomically.
    await _persist_chunks(report_id, chunk_rows)

    pages_with_chunks = len({row[0] for row in chunk_rows})
    extractor_tag = _dominant_extractor(page_results)
    logger.info(
        "ingest report=%s complete: %d chunks across %d/%d pages (extractor=%s)",
        report_id, len(chunk_rows), pages_with_chunks, pages_total, extractor_tag,
    )
    return {
        "pages_processed": pages_with_chunks,
        "chunks_inserted": len(chunk_rows),
        "pages_total":     pages_total,
        "extractor":       extractor_tag,
    }


# ── Extraction paths ────────────────────────────────────────────────────────

async def _extract_full_pdf(pdf_bytes: bytes) -> list[dict] | None:
    """Try doc-processor /v1/extract_document. Returns None to signal that
    the per-page path should run instead (disabled, oversized, or failed).

    Runs in a thread pool because httpx (sync client used in doc_extractor)
    blocks. The whole call is one HTTP round-trip per document.
    """
    loop = asyncio.get_running_loop()
    return await loop.run_in_executor(None, doc_extractor.extract_document, pdf_bytes)


async def _extract_per_page(
    pdf_bytes: bytes, force_gemini: bool = False,
) -> list[dict]:
    """Render each page to PNG and run extract_page() with bounded
    concurrency. extract_page() itself tries doc-processor /v1/extract first
    and falls back to Gemini Vision on any error.

    When `force_gemini=True`, doc-processor is skipped per-page and every page
    is sent to Gemini Vision directly. Used by the "gemini-vision" extractor
    toggle for A/B comparison.
    """
    page_pngs: list[tuple[int, bytes]] = []
    with fitz.open(stream=pdf_bytes, filetype="pdf") as doc:
        for page_num, page in enumerate(doc, start=1):
            pix = page.get_pixmap(matrix=MAT, colorspace=fitz.csRGB)
            page_pngs.append((page_num, pix.tobytes("png")))

    logger.info(
        "ingest per-page: rendered %d pages, parallelism=%d, force_gemini=%s",
        len(page_pngs), PARALLELISM, force_gemini,
    )

    sem = asyncio.Semaphore(PARALLELISM)
    loop = asyncio.get_running_loop()

    async def _process(page_num: int, png_bytes: bytes) -> dict:
        async with sem:
            page = await loop.run_in_executor(
                None,
                doc_extractor.extract_page,
                png_bytes, page_num, force_gemini,
            )
            page = dict(page or {})
            page.setdefault("page_number", page_num)
            return page

    return list(await asyncio.gather(
        *(_process(pn, png) for pn, png in page_pngs),
    ))


# ── Chunking + embedding ────────────────────────────────────────────────────

async def _chunk_and_embed(
    page_results: list[dict],
) -> list[tuple[int, int, str, np.ndarray, dict]]:
    """Convert per-page extraction output into chunk rows.

    Pipeline:
      1. Chunk every non-empty page locally (sub-page chunking via LangChain).
      2. Send ALL chunk strings in one batch to doc-processor /v1/embed.
         Single HTTP round-trip; the GPU service batches internally so a
         100-chunk doc embeds in one VRAM pass instead of 100 Vertex calls.
      3. Stitch (page, idx, text, embedding, metadata) tuples back in order.

    Returns rows sorted by (page_number, chunk_index) for deterministic insert.
    """
    # Step 1 — chunk locally, preserving (page_num, chunk_idx, metadata).
    pending: list[tuple[int, int, str, dict]] = []
    for page in page_results:
        page_num = int(page.get("page_number") or 0) or None
        content: str = page.get("content") or ""
        metadata: dict = dict(page.get("metadata") or {})
        if page_num is None or not content.strip():
            continue
        chunks = chunk_text(content)
        for idx, ch in enumerate(chunks):
            pending.append((page_num, idx, ch, metadata))

    if not pending:
        return []

    texts = [p[2] for p in pending]
    logger.info(
        "[ingest] embedding %d chunks via doc-processor /v1/embed (one batch)",
        len(texts),
    )

    # Step 2 — embed all chunks in one GPU batch. The doc-processor handles
    # internal batching so we send the entire list and get one response back.
    loop = asyncio.get_running_loop()
    vectors = await loop.run_in_executor(
        None,
        lambda: doc_extractor.embed_texts(texts, "passage"),
    )

    if len(vectors) != len(pending):
        raise RuntimeError(
            f"embed batch returned {len(vectors)} vectors for {len(pending)} chunks"
        )

    # Step 3 — assemble final rows, already in chunk order.
    rows: list[tuple[int, int, str, np.ndarray, dict]] = [
        (page_num, idx, ch, np.array(vec, dtype=np.float32), metadata)
        for (page_num, idx, ch, metadata), vec in zip(pending, vectors)
    ]
    rows.sort(key=lambda x: (x[0], x[1]))
    return rows


# ── Persistence ─────────────────────────────────────────────────────────────

async def _persist_chunks(
    report_id: UUID,
    rows: list[tuple[int, int, str, np.ndarray, dict]],
) -> None:
    """Replace this report's chunks in a single transaction. The DELETE
    keeps re-ingest idempotent — safe to retry without producing duplicates.
    """
    async with conn_ctx() as conn:
        cur = await conn.execute(
            'DELETE FROM report_chunks WHERE "ReportId" = %s',
            [str(report_id)],
        )
        deleted = cur.rowcount or 0
        for page_num, chunk_idx, content, embedding_vec, metadata in rows:
            await conn.execute(
                """
                INSERT INTO report_chunks
                    ("ReportId", "PageNumber", "ChunkIndex", "Content",
                     embedding, metadata, "CreatedAt")
                VALUES (%s, %s, %s, %s, %s, %s::jsonb, now())
                """,
                [
                    str(report_id),
                    page_num,
                    chunk_idx,
                    content,
                    embedding_vec,
                    json.dumps(metadata),
                ],
            )
        await conn.commit()
    logger.info(
        "[ingest %s] persist done: deleted %d old, inserted %d new",
        report_id, deleted, len(rows),
    )


# ── Helpers ─────────────────────────────────────────────────────────────────

def _dominant_extractor(page_results: list[dict]) -> str:
    """Pick the most common `extractor` tag across pages for output_data.

    Useful for the .NET side / dashboards to see at-a-glance which path won
    on this report. When pages came from different extractors (e.g.
    doc-processor for half, Gemini Vision for the rest) we tag it 'mixed'.
    """
    tags = [
        (p.get("metadata") or {}).get("extractor")
        for p in page_results
        if (p.get("metadata") or {}).get("extractor")
    ]
    if not tags:
        return "unknown"
    unique = set(tags)
    return tags[0] if len(unique) == 1 else "mixed"
