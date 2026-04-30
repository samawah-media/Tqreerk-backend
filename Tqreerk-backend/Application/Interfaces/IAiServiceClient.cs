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
}

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

public record SummarizeResult(Guid ReportId, string Summary, IReadOnlyList<string> KeyFindings, IReadOnlyList<string> Topics);
public record TranslateResult(Guid ReportId, string TargetLanguage, string SourceLanguage, string TranslatedFileUrl);
