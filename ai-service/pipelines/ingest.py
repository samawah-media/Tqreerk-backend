"""PDF ingestion pipeline.

Flow:
  1. Download the PDF from the given URL (or receive bytes directly).
  2. Render each page as a 150-dpi PNG using PyMuPDF.
  3. Send each PNG to Gemini Flash → rich text description.
  4. Embed the description with text-embedding-004 → vector(768).
  5. Upsert into report_pages table.
"""
import io
from uuid import UUID

import fitz  # PyMuPDF
import numpy as np
import httpx

from core.db import get_conn
from services.gemini import describe_page_image, embed_text


DPI = 150
MAT = fitz.Matrix(DPI / 72, DPI / 72)


async def ingest_report(report_id: UUID, file_url: str) -> int:
    """Download a PDF and populate report_pages. Returns number of pages processed."""
    async with httpx.AsyncClient(timeout=120) as client:
        resp = await client.get(file_url)
        resp.raise_for_status()
        pdf_bytes = resp.content

    return await ingest_report_bytes(report_id, pdf_bytes)


async def ingest_report_bytes(report_id: UUID, pdf_bytes: bytes) -> int:
    doc = fitz.open(stream=pdf_bytes, filetype="pdf")
    processed = 0

    async with get_conn() as conn:
        # Delete existing pages so re-ingestion is idempotent
        await conn.execute("DELETE FROM report_pages WHERE \"ReportId\" = %s", [str(report_id)])

        for page_num, page in enumerate(doc, start=1):
            # Render page to PNG
            pix = page.get_pixmap(matrix=MAT, colorspace=fitz.csRGB)
            png_bytes = pix.tobytes("png")

            # Extract rich text via Gemini
            content = describe_page_image(png_bytes)

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
