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
    file_url: str              # gs://bucket/reports/{report_id}/original.pdf  — input
    output_prefix: str         # gs://bucket/reports/{report_id}/translated/en/  — .NET controls this
    target_language: str       # "en" | "ar"
    source_language: str = "ar"


class TranslateResponse(BaseModel):
    report_id: UUID
    target_language: str
    translated_file_url: str   # exact GCS URI of the output PDF — store in ReportTranslation
