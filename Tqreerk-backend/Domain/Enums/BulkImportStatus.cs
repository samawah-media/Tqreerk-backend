namespace Taqreerk.Domain.Enums;

/// <summary>
/// Lifecycle of a single bulk-import job — owns the whole batch (one Excel
/// upload). Per-row state lives on <c>BulkImportItem.Stage</c>.
/// </summary>
public enum BulkImportStatus
{
    /// Excel parsed, items queued; the background processor hasn't picked
    /// the job up yet.
    Pending,
    /// At least one row is in flight (Uploading / Ingesting / Summarizing).
    Processing,
    /// Every row reached a terminal state (Completed or Failed). The job
    /// being Completed doesn't imply every row succeeded — check items.
    Completed,
    /// The whole batch couldn't be processed (e.g. Excel was malformed
    /// before any row started).
    Failed,
}

/// <summary>
/// Per-row pipeline stage. Rows progress through these in order; on any
/// failure the row jumps straight to Failed and ErrorMessage is populated.
/// </summary>
public enum BulkImportItemStage
{
    /// Parsed from the Excel, validated, awaiting the processor.
    Pending,
    /// Fetching the source PDF from <c>FileUrl</c> and uploading to GCS.
    Uploading,
    /// PDF stored, Report row created, ingest job queued on the AI service.
    Ingesting,
    /// Ingest finished; summarize job queued on the AI service.
    Summarizing,
    /// Report is Published with summary copied into report_ai_contents.
    Completed,
    /// One of the stages above failed — see ErrorMessage.
    Failed,
}
