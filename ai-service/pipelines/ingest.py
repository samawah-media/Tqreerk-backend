"""PDF ingestion pipeline.

Flow:
  1. Download the PDF from GCS (gs://) or HTTPS.
  2. Render each page as a 150-dpi PNG using PyMuPDF.
  3. Send each PNG to Gemini Flash → rich text description.
  4. Embed the description with text-embedding-004 → vector(768).
  5. Upsert into report_pages table.
"""
from uuid import UUID

import fitz  # PyMuPDF
import numpy as np
import httpx
from google.cloud import storage

from core.db import conn_ctx
from services.gemini import describe_page_image, embed_text


DPI = 150
MAT = fitz.Matrix(DPI / 72, DPI / 72)

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

    Returns number of pages processed.
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
    doc = fitz.open(stream=pdf_bytes, filetype="pdf")
    processed = 0

    async with conn_ctx() as conn:
        # Delete existing pages so re-ingestion is idempotent
        await conn.execute("DELETE FROM report_pages WHERE \"ReportId\" = %s", [str(report_id)])

        for page_num, page in enumerate(doc, start=1):
            # Render page to PNG
            pix = page.get_pixmap(matrix=MAT, colorspace=fitz.csRGB)
            png_bytes = pix.tobytes("png")

            # Extract rich text via Gemini
            content = describe_page_image(png_bytes)

            # Skip pages with no extractable content (blank pages, visual-only).
            # Storing them with no text/embedding would fail the embed call AND
            # poison hybrid search results.
            if not content or not content.strip():
                continue

            # Embed the description
            embedding = embed_text(content)
            embedding_vec = np.array(embedding, dtype=np.float32)

            await conn.execute(
                """
                INSERT INTO report_pages ("ReportId", "PageNumber", "Content", embedding, "CreatedAt")
                VALUES (%s, %s, %s, %s, now())
                """,
                [str(report_id), page_num, content, embedding_vec],
            )
            processed += 1

        await conn.commit()

    doc.close()
    return processed
