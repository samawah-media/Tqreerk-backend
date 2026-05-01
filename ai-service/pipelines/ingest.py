"""PDF ingestion pipeline.

Primary path (GPU)
==================
When DOC_PROCESSOR_ENABLED=true and extractor != "gemini-vision", ingest
delegates entirely to the doc-processor /v1/ingest endpoint. The GPU
service handles download → extract → chunk → embed → persist in one call;
the ai-service only receives stats and marks the job Complete. No PDF bytes
or chunk vectors cross the network twice.

Fallback path (Gemini Vision / legacy)
=======================================
Used when doc-processor is disabled, unreachable, or the caller explicitly
requests extractor="gemini-vision":
  1. Download PDF from GCS or HTTPS.
  2. Render each page to PNG (PyMuPDF).
  3. Describe each page via Gemini Vision.
  4. Chunk text locally (~500-token splits via LangChain).
  5. Embed all chunks via Vertex gemini-embedding-001.
  6. Write report_chunks in one transaction (idempotent — DELETE first).
"""
import asyncio
import json
import logging
from uuid import UUID

import fitz  # PyMuPDF
import httpx
import numpy as np
import sentry_sdk
from google.cloud import storage

from core.chunking import chunk_text
from core.config import settings
from core.db import conn_ctx
from services import doc_extractor, embed, observability as obs

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
    """Ingest a PDF and populate report_chunks.

    Primary path — GPU (doc-processor /v1/ingest):
        When DOC_PROCESSOR_ENABLED=true and extractor != "gemini-vision",
        the entire pipeline (download, extract, chunk, embed, persist) runs
        on the GPU Cloud Run service. This function receives only stats.

    Fallback path — Gemini Vision:
        Used when doc-processor is disabled or extractor="gemini-vision".
        The PDF is downloaded here, each page is described by Gemini Vision,
        chunks are split locally, embedded via Vertex, and persisted.

    Returns:
        {
          "pages_processed":  int,
          "chunks_inserted":  int,
          "pages_total":      int,
          "extractor":        str,
        }
    """
    trace = obs.trace(
        name="ingest",
        input={"file_url": file_url, "extractor": extractor},
        metadata={"report_id": str(report_id)},
        tags=["ingest", f"extractor:{extractor}"],
    )

    # ── Primary path: full GPU ingest ────────────────────────────────────────
    if (
        extractor != "gemini-vision"
        and settings.doc_processor_enabled
        and settings.doc_processor_url
    ):
        loop = asyncio.get_running_loop()
        with sentry_sdk.start_span(op="ingest.gpu", description="/v1/ingest"), \
             obs.span(trace, name="gpu_ingest",
                      input={"file_url": file_url}) as sp:
            logger.info("[ingest %s] GPU path: calling /v1/ingest", report_id)
            result = await loop.run_in_executor(
                None,
                doc_extractor.ingest_via_gpu,
                str(report_id),
                file_url,
            )
            sp.update(output=result)
        logger.info(
            "[ingest %s] GPU ingest done: %d chunks / %d pages (extractor=%s)",
            report_id,
            result.get("chunks_inserted", 0),
            result.get("pages_processed", 0),
            result.get("extractor", "?"),
        )
        try:
            trace.update(output=result)
            obs.flush()
        except Exception:
            pass
        return {
            "pages_processed": result.get("pages_processed", 0),
            "chunks_inserted": result.get("chunks_inserted", 0),
            "pages_total":     result.get("pages_total", 0),
            "extractor":       result.get("extractor", "doc-processor-v1"),
        }

    # ── Fallback path: Gemini Vision ─────────────────────────────────────────
    logger.info(
        "[ingest %s] Gemini Vision path (extractor=%s, doc_processor_enabled=%s)",
        report_id, extractor, settings.doc_processor_enabled,
    )
    result = await _ingest_gemini_vision(report_id, file_url, trace)
    try:
        trace.update(output=result)
        obs.flush()
    except Exception:
        pass
    return result


async def _ingest_gemini_vision(
    report_id: UUID, file_url: str, trace,
) -> dict:
    """Legacy ingest: download PDF → Gemini Vision per-page → chunk → embed → persist."""
    with sentry_sdk.start_span(op="ingest.download", description=file_url), \
         obs.span(trace, name="download", input={"file_url": file_url}) as sp:
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
        sp.update(output={"bytes": len(pdf_bytes)})

    with fitz.open(stream=pdf_bytes, filetype="pdf") as doc:
        pages_total = len(doc)

    with sentry_sdk.start_span(op="ingest.extract", description="gemini-vision"), \
         obs.span(trace, name="extract",
                  input={"extractor": "gemini-vision", "pages_total": pages_total}) as sp:
        page_results = await _extract_per_page(pdf_bytes, force_gemini=True)
        sp.update(output={
            "n_pages": len(page_results),
            "extractor_used": _dominant_extractor(page_results),
        })

    if not page_results:
        logger.warning(
            "[ingest %s] no pages extracted (%d total in PDF)", report_id, pages_total,
        )

    with sentry_sdk.start_span(op="ingest.chunk_and_embed"), \
         obs.span(trace, name="chunk_and_embed",
                  input={"n_pages": len(page_results)}) as sp:
        chunk_rows = await _chunk_and_embed(page_results)
        sp.update(output={"n_chunks": len(chunk_rows)})

    with sentry_sdk.start_span(op="ingest.persist"), \
         obs.span(trace, name="persist", input={"n_rows": len(chunk_rows)}):
        await _persist_chunks(report_id, chunk_rows)

    pages_with_chunks = len({row[0] for row in chunk_rows})
    extractor_tag = _dominant_extractor(page_results)
    logger.info(
        "ingest report=%s complete (gemini-vision): %d chunks / %d/%d pages",
        report_id, len(chunk_rows), pages_with_chunks, pages_total,
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
    """Convert Gemini Vision per-page output into chunk rows (fallback path only).

    The GPU path never calls this — doc-processor chunks and embeds on the
    GPU side. This is used only when extractor="gemini-vision" or doc-processor
    is disabled. Gemini Vision returns plain text (no structured blocks), so
    we always use the recursive char splitter here.

    Returns rows sorted by (page_number, chunk_index) for deterministic insert.
    """
    pending: list[tuple[int, int, str, dict]] = []
    for page in page_results:
        page_num = int(page.get("page_number") or 0) or None
        content: str = page.get("content") or ""
        metadata: dict = dict(page.get("metadata") or {})
        if page_num is None:
            continue
        if content.strip():
            for idx, ch in enumerate(chunk_text(content)):
                pending.append((page_num, idx, ch, metadata))

    if not pending:
        return []

    texts = [p[2] for p in pending]
    logger.info(
        "[ingest] embedding %d chunks via Vertex gemini-embedding-001 (RETRIEVAL_DOCUMENT)",
        len(texts),
    )

    # Step 2 — embed all chunks via the managed embedding API. The genai
    # SDK batches server-side; we send one HTTP call. _call_with_retry inside
    # the wrapper handles SSL EOFs and connection resets the same way as
    # every other Gemini call.
    loop = asyncio.get_running_loop()
    vectors = await loop.run_in_executor(
        None, embed.embed_passages, texts,
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
