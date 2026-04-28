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
    file_url: str           # gs://bucket/reports/{report_id}/original.pdf  — input from GCS
    output_prefix: str      # gs://bucket/reports/{report_id}/translated/  — .NET-owned base path (must end with /)
                            # Python appends "{detected_target_lang}/" before calling Google Translate.


class TranslateResponse(BaseModel):
    report_id: UUID
    source_language: str       # detected source language e.g. "ar"
    target_language: str       # what we translated to e.g. "en"
    translated_file_url: str   # exact GCS URI of the output PDF — store in ReportTranslation


# ── Bulk (async fire-and-forget via ai_jobs table) ───────────────────────────

class BulkIngestItem(BaseModel):
    report_id: UUID
    file_url: str


class BulkIngestRequest(BaseModel):
    items: list[BulkIngestItem]
    user_id: UUID | None = None             # optional — who triggered this batch
    organization_id: UUID | None = None     # optional — org context for the batch


class BulkTranslateItem(BaseModel):
    report_id: UUID
    file_url: str
    output_prefix: str


class BulkTranslateRequest(BaseModel):
    items: list[BulkTranslateItem]
    user_id: UUID | None = None
    organization_id: UUID | None = None


class CreatedJob(BaseModel):
    job_id: UUID
    report_id: UUID
    status: str = "Pending"


class BulkJobsResponse(BaseModel):
    """Returned synchronously when a bulk request is accepted.

    Each job is processed in the background; poll GET /jobs/{job_id} for results.
    """
    jobs: list[CreatedJob]


class JobStatusResponse(BaseModel):
    job_id: UUID
    report_id: UUID | None
    job_type: str                       # "Ingest" | "Translate" | etc.
    status: str                         # "Pending" | "Processing" | "Completed" | "Failed"
    error_message: str | None = None
    output_data: dict | None = None     # parsed jsonb output (varies by job_type)
    started_at: str | None = None       # ISO 8601
    completed_at: str | None = None
