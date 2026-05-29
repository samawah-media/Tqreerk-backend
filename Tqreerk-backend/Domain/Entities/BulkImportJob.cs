using Taqreerk.Domain.Common;
using Taqreerk.Domain.Enums;

namespace Taqreerk.Domain.Entities;

/// <summary>
/// One Excel upload from an admin = one bulk-import job. Tracks the batch
/// lifecycle so the admin UI can poll for live progress + show a history
/// of past imports without us having to re-derive state from items.
/// </summary>
public class BulkImportJob : BaseEntity
{
    public Guid CreatedByUserId { get; set; }
    public Guid OrganizationId { get; set; }

    public BulkImportStatus Status { get; set; } = BulkImportStatus.Pending;

    /// <summary>Total rows parsed out of the Excel (≤ 10).</summary>
    public int TotalCount { get; set; }

    /// Counters refreshed by the processor as items flip to terminal state.
    /// Cheaper than aggregating items on every poll; tolerated drift is
    /// fine because the items table is the source of truth.
    public int CompletedCount { get; set; }
    public int FailedCount { get; set; }

    /// Original filename of the uploaded Excel — purely so the history
    /// view can label the row as "<file.xlsx> (5 reports)".
    public string? SourceFileName { get; set; }

    /// Free-form error when the whole batch couldn't even start
    /// (corrupt Excel, missing required headers).
    public string? ErrorMessage { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// Cumulative seconds spent processing across all runs (retries).
    /// The current run's elapsed time is NOT included here — add
    /// (now − StartedAt) on the client to get the true total.
    public long AccumulatedSeconds { get; set; }

    public User? CreatedByUser { get; set; }
    public Organization? Organization { get; set; }
    public ICollection<BulkImportItem> Items { get; set; } = [];
}
