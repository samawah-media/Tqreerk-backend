from typing import Literal
from uuid import UUID
from pydantic import BaseModel, Field


class IngestRequest(BaseModel):
    report_id: UUID
    file_url: str
    # "auto"          → current behaviour: try doc-processor full-PDF, fall back per-page (Gemini Vision).
    # "doc-processor" → force doc-processor only; if it returns nothing, the job fails.
    # "gemini-vision" → bypass doc-processor entirely, render every page and use Gemini Vision only.
    # Use "doc-processor" and "gemini-vision" on the same report to A/B compare extractor quality.
    extractor: Literal["auto", "doc-processor", "gemini-vision"] = "auto"


class IngestResponse(BaseModel):
    """202 — ingest queued. Poll /api/ai/reports/jobs/{job_id} for completion."""
    report_id: UUID
    job_id: UUID
    status: str = "Pending"


class ReportPageContent(BaseModel):
    page_number: int
    content: str


class ReportPagesResponse(BaseModel):
    report_id: UUID
    page_count: int
    pages: list[ReportPageContent]


class SummarizeRequest(BaseModel):
    report_id: UUID


class IndicatorItem(BaseModel):
    name: str
    value: str
    unit: str | None = None
    time_period: str | None = None
    context: str | None = None


class TrendItem(BaseModel):
    topic: str
    direction: str
    time_span: str | None = None
    magnitude: str | None = None
    explanation: str | None = None


class SummarizeResponse(BaseModel):
    """Combined summary + insights output. One Gemini call now produces all
    five fields (summary, key_findings, topics, indicators, trends), and they
    all get returned to the caller so the .NET finalizer (or any direct
    consumer) can persist them in one round-trip.

    `summary` is a list of 3-7 bullet points (not a paragraph blob). The
    frontend renders them as discrete points; the length window keeps the
    output skimmable without losing nuance for longer reports."""
    report_id: UUID
    summary: list[str] = Field(min_length=3, max_length=7)
    key_findings: list[str]
    topics: list[str]
    indicators: list[IndicatorItem] = []
    trends: list[TrendItem] = []


class TranslateRequest(BaseModel):
    report_id: UUID
    file_url: str           # gs://bucket/reports/{report_id}/original.pdf  — input from GCS
    output_prefix: str      # gs://bucket/reports/{report_id}/translated/  — .NET-owned base path (must end with /)
                            # Python appends "{detected_target_lang}/" before calling Google Translate.
    # Optional language overrides. When omitted, the worker auto-detects
    # source_language from the report's stored chunks and flips it for
    # target_language ("ar" → "en", anything else → "ar").
    source_language: str | None = None
    target_language: str | None = None


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


class BulkSummarizeItem(BaseModel):
    """Summarize-only items don't need file_url — the chunks are already
    in report_chunks. Just the report_id is enough for the worker to
    fetch text and run the combined summary+insights call."""
    report_id: UUID


class BulkSummarizeRequest(BaseModel):
    items: list[BulkSummarizeItem]


class BulkTranslateItem(BaseModel):
    report_id: UUID
    file_url: str
    output_prefix: str


class BulkTranslateRequest(BaseModel):
    items: list[BulkTranslateItem]


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


# ── Insights (per report) ────────────────────────────────────────────────────

class InsightsRequest(BaseModel):
    report_id: UUID


class Indicator(BaseModel):
    name: str
    value: str
    unit: str | None = None
    time_period: str | None = None
    context: str | None = None


class Trend(BaseModel):
    topic: str
    direction: str                       # increasing | decreasing | stable | volatile | mixed
    time_span: str | None = None
    magnitude: str | None = None
    explanation: str | None = None


class InsightsResponse(BaseModel):
    report_id: UUID
    indicators: list[Indicator]
    trends: list[Trend]


# ── Comparison (multi-report) ────────────────────────────────────────────────

class CompareRequest(BaseModel):
    report_ids: list[UUID]               # 2 or more


class SharedIndicator(BaseModel):
    name: str
    values_per_report: str               # e.g. "Report 1: 4.2%, Report 2: 3.8%"


class ReportSimilarity(BaseModel):
    report_a: UUID
    report_b: UUID
    score: float                         # cosine similarity, -1 .. 1


class CompareResponse(BaseModel):
    report_ids: list[UUID]
    common_topics: list[str]
    key_differences: list[str]
    shared_indicators: list[SharedIndicator]
    overall_summary: str
    similarities: list[ReportSimilarity] # pairwise embedding cosine similarity
