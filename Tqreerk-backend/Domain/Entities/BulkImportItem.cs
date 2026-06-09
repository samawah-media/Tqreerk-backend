using Taqreerk.Domain.Common;
using Taqreerk.Domain.Enums;

namespace Taqreerk.Domain.Entities;

/// <summary>
/// One row of an admin-uploaded Excel = one would-be report. Tracked from
/// pre-parse all the way to Published so the admin UI can show per-row
/// progress live (Uploading → Ingesting → Summarizing → Completed/Failed).
/// </summary>
public class BulkImportItem : BaseEntity
{
    public Guid JobId { get; set; }
    public BulkImportJob Job { get; set; } = null!;

    /// <summary>1-based row number from the Excel (data row, not header).</summary>
    public int RowIndex { get; set; }

    public BulkImportItemStage Stage { get; set; } = BulkImportItemStage.Pending;

    // ── Snapshot of the Excel cells (so the UI can render the row even
    //    before the Report is created, and we can show "what was the
    //    title?" for failed rows). ──────────────────────────────────────
    public string Title { get; set; } = string.Empty;
    public string TitleEn { get; set; } = string.Empty;
    public string FileUrl { get; set; } = string.Empty;
    public string? ReportType { get; set; }
    public string? Source { get; set; }
    public string? Authors { get; set; }
    public string? OriginalLanguage { get; set; }
    public int? PublicationYear { get; set; }
    public string? SectorNameAr { get; set; }
    public string? CountryNameAr { get; set; }
    public string? Keywords { get; set; }

    // ── Created by the processor ─────────────────────────────────────────
    /// <summary>Set once the Report row has been inserted (after upload).</summary>
    public Guid? ReportId { get; set; }
    public Report? Report { get; set; }

    /// <summary>AI-service ingest job id; populated after /bulk/ingest accepts.</summary>
    public Guid? IngestJobId { get; set; }

    /// <summary>AI-service summarize job id; populated after /bulk/summarize accepts.</summary>
    public Guid? SummarizeJobId { get; set; }

    public string? ErrorMessage { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
