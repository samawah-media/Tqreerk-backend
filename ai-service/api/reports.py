"""Report AI endpoints — ingest, summarize, translate."""
from fastapi import APIRouter, Depends, HTTPException
from psycopg import AsyncConnection

from core.db import get_conn
from models.ingest import (
    IngestRequest,
    IngestResponse,
    SummarizeRequest,
    SummarizeResponse,
    TranslateRequest,
    TranslateResponse,
)
from pipelines.ingest import ingest_report
from services.gemini import summarize_report
from services.translate import detect_language, translate_pdf

router = APIRouter(prefix="/reports", tags=["reports"])


@router.post("/ingest", response_model=IngestResponse, status_code=202)
async def ingest(body: IngestRequest):
    """Trigger PDF ingestion: extract text via Gemini and store embeddings."""
    pages = await ingest_report(body.report_id, body.file_url)
    return IngestResponse(report_id=body.report_id, pages_processed=pages)


@router.post("/summarize", response_model=SummarizeResponse)
async def summarize(
    body: SummarizeRequest,
    conn: AsyncConnection = Depends(get_conn),
):
    """Generate executive summary and key findings from stored page content."""
    cur = await conn.execute(
        """
        SELECT "Content"
        FROM report_pages
        WHERE "ReportId" = %s
        ORDER BY "PageNumber"
        """,
        [str(body.report_id)],
    )
    rows = await cur.fetchall()
    if not rows:
        raise HTTPException(status_code=404, detail="Report pages not found — ingest first")

    pages_content = [r[0] for r in rows]
    result = summarize_report(pages_content)
    return SummarizeResponse(
        report_id=body.report_id,
        summary=result.summary,
        key_findings=result.key_findings,
        topics=result.topics,
    )


@router.post("/translate", response_model=TranslateResponse)
async def translate(
    body: TranslateRequest,
    conn: AsyncConnection = Depends(get_conn),
):
    """Translate the report PDF using Google Cloud Translation API v3 Document Translation.

    Auto-detects source language from stored page content (ingest first).
    Arabic → English, anything else → Arabic.
    Returns the GCS URI of the translated PDF; store it in ReportTranslation.
    """
    # Use stored page text for language detection — no extra API round-trip needed
    cur = await conn.execute(
        'SELECT "Content" FROM report_pages WHERE "ReportId" = %s ORDER BY "PageNumber" LIMIT 1',
        [str(body.report_id)],
    )
    row = await cur.fetchone()
    if not row:
        raise HTTPException(status_code=404, detail="Report pages not found — ingest first")

    source_language = detect_language(row[0])
    target_language = "en" if source_language == "ar" else "ar"

    translated_url = translate_pdf(
        gcs_input_uri=body.file_url,
        output_prefix=body.output_prefix,
        source_language=source_language,
        target_language=target_language,
    )
    return TranslateResponse(
        report_id=body.report_id,
        source_language=source_language,
        target_language=target_language,
        translated_file_url=translated_url,
    )
