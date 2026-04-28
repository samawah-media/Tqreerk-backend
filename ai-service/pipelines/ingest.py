"""PDF ingestion pipeline.

Flow:
  1. Download the PDF from GCS (gs://) or HTTPS.
  2. Render every page as a 150-dpi PNG using PyMuPDF (fast, in-process).
  3. Process pages in parallel: Gemini Vision → embedding, capped at PARALLELISM
     concurrent calls so we stay under Vertex AI / Gemini RPM quotas.
  4. Insert all results into report_pages table in one DB transaction.

Why parallel: each Gemini call is ~2.5 s of network/compute. Sequential ingest
on a 50-page Arabic report = ~125 s. With PARALLELISM=5 it drops to ~25 s.
"""
import asyncio
import logging
from uuid import UUID

import fitz  # PyMuPDF
import httpx
import numpy as np
from google.cloud import storage

from core.db import conn_ctx
from services.gemini import describe_page_image, embed_text

logger = logging.getLogger(__name__)

DPI = 150
MAT = fitz.Matrix(DPI / 72, DPI / 72)

# Max concurrent Gemini calls during ingest. Tunable per project quota.
# Vertex AI Gemini Flash default RPM is plenty for 5 in-flight requests.
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


async def ingest_report(report_id: UUID, file_url: str) -> int:
    """Download a PDF (gs:// or https://) and populate report_pages.

    Returns number of pages successfully processed (excludes blank/skipped pages).
    """
    if file_url.startswith("gs://"):
        pdf_bytes = _download_from_gcs(file_url)
    else:
        async with httpx.AsyncClient(timeout=120) as client:
            resp = await client.get(file_url)
            resp.raise_for_status()
            pdf_bytes = resp.content

    return await ingest_report_bytes(report_id, pdf_bytes)


async def ingest_report_bytes(report_id: UUID, pdf_bytes: bytes) -> int:
    # ── Step 1: render every page as a PNG up-front (fast, in-memory) ───────
    doc = fitz.open(stream=pdf_bytes, filetype="pdf")
    page_pngs: list[tuple[int, bytes]] = []
    for page_num, page in enumerate(doc, start=1):
        pix = page.get_pixmap(matrix=MAT, colorspace=fitz.csRGB)
        page_pngs.append((page_num, pix.tobytes("png")))
    doc.close()

    logger.info("ingest report=%s rendered %d pages, processing with parallelism=%d",
                report_id, len(page_pngs), PARALLELISM)

    # ── Step 2: process pages in parallel ────────────────────────────────────
    # describe_page_image / embed_text are SYNC (network calls inside the
    # google-genai SDK), so we run them in a thread pool. asyncio.Semaphore
    # caps concurrent threads under Gemini RPM limits.
    sem = asyncio.Semaphore(PARALLELISM)
    loop = asyncio.get_running_loop()

    async def process_page(page_num: int, png_bytes: bytes):
        async with sem:
            content = await loop.run_in_executor(None, describe_page_image, png_bytes)
            if not content or not content.strip():
                return None  # blank/visual-only page → skip
            embedding = await loop.run_in_executor(None, embed_text, content)
            return page_num, content, np.array(embedding, dtype=np.float32)

    results = await asyncio.gather(
        *(process_page(pn, png) for pn, png in page_pngs),
        return_exceptions=True,
    )

    # Drop None (skipped) and Exception (per-page failures don't kill the batch)
    successes: list[tuple[int, str, np.ndarray]] = []
    for r in results:
        if isinstance(r, Exception):
            logger.warning("ingest report=%s a page failed: %s", report_id, r)
            continue
        if r is None:
            continue
        successes.append(r)

    successes.sort(key=lambda x: x[0])  # write in page order

    # ── Step 3: bulk insert in a single transaction ──────────────────────────
    async with conn_ctx() as conn:
        await conn.execute(
            'DELETE FROM report_pages WHERE "ReportId" = %s',
            [str(report_id)],
        )
        for page_num, content, embedding_vec in successes:
            await conn.execute(
                """
                INSERT INTO report_pages ("ReportId", "PageNumber", "Content", embedding, "CreatedAt")
                VALUES (%s, %s, %s, %s, now())
                """,
                [str(report_id), page_num, content, embedding_vec],
            )
        await conn.commit()

    logger.info("ingest report=%s complete: %d/%d pages stored",
                report_id, len(successes), len(page_pngs))
    return len(successes)
