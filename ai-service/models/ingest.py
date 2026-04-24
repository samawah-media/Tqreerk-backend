from uuid import UUID
from pydantic import BaseModel


class IngestRequest(BaseModel):
    report_id: UUID
    file_url: str


class IngestResponse(BaseModel):
    report_id: UUID
    pages_processed: int
    status: str = "ok"


class SummarizeRequest(BaseModel):
    report_id: UUID


class SummarizeResponse(BaseModel):
    report_id: UUID
    summary: str
    key_findings: list[str]
    topics: list[str]


class TranslateRequest(BaseModel):
    report_id: UUID
    file_url: str       # gs://bucket/reports/{report_id}/original.pdf  — input
    output_prefix: str  # gs://bucket/reports/{report_id}/translated/{lang}/  — .NET controls this


class TranslateResponse(BaseModel):
    report_id: UUID
    source_language: str    # detected source language e.g. "ar"
    target_language: str    # what we translated to e.g. "en"
    translated_file_url: str  # exact GCS URI of the output PDF — store in ReportTranslation
