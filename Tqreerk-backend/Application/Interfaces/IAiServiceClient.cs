namespace Taqreerk.Application.Interfaces;

/// Typed HTTP client for the external Python ai-service.
/// All methods throw on transport / non-2xx and the caller (ReportProcessingWorker)
/// catches & marks the AiJob as Failed. The body of each error is preserved on the
/// thrown exception so we can surface it to the user.
///
/// Ingest contract change (2026-04): the Python service now treats /reports/ingest
/// as fire-and-forget. The HTTP call returns 202 with a job_id; the ingest itself
/// runs in the background. Callers must poll GetJobStatusAsync(jobId) until the
/// job reaches Completed/Failed before calling Summarize or Translate (which both
/// require the ingested page content).
public interface IAiServiceClient
{
    /// POST /reports/ingest — schedule PDF extraction (fire-and-forget). Returns
    /// immediately with the AI-service's job_id. Caller MUST poll
    /// GetJobStatusAsync until the job is Completed before calling Summarize.
    /// `fileUrl` should be the gs:// URI (NOT a signed URL) — the ai-service uses
    /// service-account credentials to read directly from the bucket.
    Task<IngestEnqueueResult> IngestAsync(Guid reportId, string fileUrl, CancellationToken ct = default);

    /// GET /reports/jobs/{jobId} — poll the AI service's view of a previously
    /// queued job. Used to wait on /ingest completion. Status values are the
    /// strings "Pending" | "Processing" | "Completed" | "Failed".
    Task<AiJobStatusSnapshot> GetJobStatusAsync(Guid jobId, CancellationToken ct = default);

    /// POST /reports/summarize — uses already-ingested report_pages content.
    /// Calling this before ingest completes returns 202 from the ai-service.
    Task<SummarizeResult> SummarizeAsync(Guid reportId, CancellationToken ct = default);

    /// POST /reports/translate — translates the PDF document and returns the
    /// translated GCS URL. Requires the original PDF at `fileUrl` (gs://) and
    /// an `outputPrefix` (gs://) directory the service can write to. The Python
    /// service auto-detects source language from already-ingested pages and
    /// picks the target (Arabic ↔ English), so we no longer pass them.
    Task<TranslateResult> TranslateAsync(
        Guid reportId,
        string fileUrl,
        string outputPrefix,
        CancellationToken ct = default);

    /// POST /api/ai/reports/compare — runs the two-layer comparison on
    /// 2..4 already-ingested + summarized reports. Layer 1 is pairwise
    /// cosine similarity over chunk embeddings (cheap, runs in
    /// pgvector); layer 2 is Gemini structured output over the cached
    /// summaries + key findings (common topics, key differences,
    /// shared indicators). Throws on transport / non-2xx so the caller
    /// can surface the error verbatim to the user.
    Task<CompareResult> CompareAsync(
        IReadOnlyList<Guid> reportIds, CancellationToken ct = default);

    /// POST /api/ai/reports/bulk/ingest — enqueue ingest jobs for many
    /// reports in one round-trip. Each item triggers the GPU-direct
    /// ingest pipeline; responses are async (one job_id per item) and
    /// callers must poll <see cref="GetJobStatusAsync"/> per job id.
    Task<IReadOnlyList<BulkJobResult>> BulkIngestAsync(
        IReadOnlyList<BulkIngestItem> items, CancellationToken ct = default);

    /// POST /api/ai/reports/bulk/summarize — enqueue Summarization jobs
    /// for many already-ingested reports. The AI worker picks them up on
    /// its next poll cycle. Caller polls <see cref="GetJobStatusAsync"/>
    /// per returned job id.
    Task<IReadOnlyList<BulkJobResult>> BulkSummarizeAsync(
        IReadOnlyList<Guid> reportIds, CancellationToken ct = default);
}

/// One row of the /bulk/ingest request — exactly what the Python side
/// expects (matches BulkIngestItemRequest / IngestRequest pairing).
public record BulkIngestItem(Guid ReportId, string FileUrl);

/// One row of any /bulk/* response. The Python service returns
/// {"jobs":[{job_id, report_id}, ...]}; we flatten and surface the same
/// shape regardless of which bulk endpoint was called.
public record BulkJobResult(Guid JobId, Guid ReportId);

/// Returned synchronously from POST /reports/ingest. The actual ingest runs in
/// the background — caller polls GetJobStatusAsync until done.
public record IngestEnqueueResult(Guid ReportId, Guid JobId, string Status);

public record AiJobStatusSnapshot(
    Guid JobId,
    Guid? ReportId,
    string JobType,
    string Status,
    string? ErrorMessage,
    /// Free-form per-job-type payload. For Ingestion this is { pages_processed: int }.
    IReadOnlyDictionary<string, object?>? OutputData);

public record SummarizeResult(
    Guid ReportId,
    string Summary,
    IReadOnlyList<string> KeyFindings,
    IReadOnlyList<string> Topics,
    /// Raw JSON arrays — Indicators / Trends arrive as arrays of objects from
    /// Gemini's structured output. We pass them through as raw text so the
    /// .NET side can persist them verbatim into the jsonb columns without
    /// having to re-shape every nested field.
    string IndicatorsJson,
    string TrendsJson);
public record TranslateResult(Guid ReportId, string TargetLanguage, string SourceLanguage, string TranslatedFileUrl);

/// Compare layer 1 — one entry per (reportA, reportB) ordered pair the
/// Python service returns. Score is in [0,1]; higher means more similar.
public record CompareSimilarityPair(Guid ReportIdA, Guid ReportIdB, double Score);

/// Result of the AI comparison. The two layers are kept on distinct
/// fields so the persisted row can drop layer 1 (cheap to recompute)
/// while keeping the expensive Gemini layer-2 output cached.
public record CompareResult(
    IReadOnlyList<Guid> ReportIds,
    IReadOnlyList<CompareSimilarityPair> Similarities,
    /// Raw JSON object for the Gemini structured output. Persisted as
    /// jsonb on our side and rendered verbatim by the frontend, so we
    /// pass the raw text through here to avoid pinning the DTO shape
    /// to a Gemini schema that may evolve.
    string QualitativeJson);
