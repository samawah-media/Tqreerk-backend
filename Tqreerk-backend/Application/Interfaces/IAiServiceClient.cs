namespace Taqreerk.Application.Interfaces;

/// Typed HTTP client for the external Python ai-service.
/// All methods throw on transport / non-2xx and the caller (ReportProcessingWorker)
/// catches & marks the AiJob as Failed. The body of each error is preserved on the
/// thrown exception so we can surface it to the user.
public interface IAiServiceClient
{
    /// POST /reports/ingest — extract text from the PDF, persist into report_pages.
    /// `fileUrl` should be the gs:// URI (NOT a signed URL) — the ai-service uses
    /// service-account credentials to read directly from the bucket.
    Task<IngestResult> IngestAsync(Guid reportId, string fileUrl, CancellationToken ct = default);

    /// POST /reports/summarize — uses already-ingested report_pages content.
    /// Calling this before ingest will return 404 from the ai-service.
    Task<SummarizeResult> SummarizeAsync(Guid reportId, CancellationToken ct = default);

    /// POST /reports/translate — translates the PDF document and returns the
    /// translated GCS URL. Requires the original PDF at `fileUrl` (gs://) and
    /// an `outputPrefix` (gs://) directory the service can write to.
    Task<TranslateResult> TranslateAsync(
        Guid reportId,
        string fileUrl,
        string outputPrefix,
        string targetLanguage,
        string sourceLanguage,
        CancellationToken ct = default);
}

public record IngestResult(Guid ReportId, int PagesProcessed, string Status);
public record SummarizeResult(Guid ReportId, string Summary, IReadOnlyList<string> KeyFindings, IReadOnlyList<string> Topics);
public record TranslateResult(Guid ReportId, string TargetLanguage, string SourceLanguage, string TranslatedFileUrl);
