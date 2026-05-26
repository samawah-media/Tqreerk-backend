using Taqreerk.Domain.Common;
using Taqreerk.Domain.Enums;

namespace Taqreerk.Domain.Entities;

/// One row per "this report is pinned to this section". A single report
/// can be featured in multiple sections at once (e.g. HomepageHero +
/// SectorTop) — each combination is its own row.
///
/// Scheduling: FeaturedFrom / FeaturedUntil are optional. The expiry
/// worker deactivates rows whose Until has passed. A null FeaturedFrom
/// means "starts immediately"; a null FeaturedUntil means "no end date".
///
/// We use BaseEntity (not SoftDeletable) — feature placement is editorial
/// and should not silently linger after a delete; if we ever need a
/// history table, that's a separate concern.
public class FeaturedReport : BaseEntity
{
    public Guid ReportId { get; set; }
    public FeaturedSection Section { get; set; }

    /// Position within the section. Lower = more prominent. The admin
    /// page rebalances values on drag-drop so they stay dense.
    public int Position { get; set; }

    /// Optional scheduling window. Both inclusive on the start side and
    /// exclusive on the end side; the expiry worker compares against
    /// DateTime.UtcNow.
    public DateTime? FeaturedFrom { get; set; }
    public DateTime? FeaturedUntil { get; set; }

    /// True while the row is live. The expiry worker flips this to false
    /// when FeaturedUntil passes. The admin page can also toggle directly.
    public bool IsActive { get; set; } = true;

    public Guid? CreatedByUserId { get; set; }

    public Report Report { get; set; } = null!;
    public User? CreatedByUser { get; set; }
}
