using Taqreerk.Domain.Common;
using Taqreerk.Domain.Enums;

namespace Taqreerk.Domain.Entities;

/// One row per "the org asked for this report to be featured". Status
/// flows Pending → Approved | Rejected; only one Pending row may exist
/// for a given report at a time (enforced by a partial unique index in
/// the configuration). On Approved, a corresponding FeaturedReport row
/// is auto-created so the editorial side doesn't have to copy data over
/// manually — the admin can still tune the section/window from the
/// Featured curation page afterwards.
public class ReportFeatureRequest : AuditableEntity
{
    public Guid ReportId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid RequestedByUserId { get; set; }

    /// Optional copy from the org explaining why this report should be
    /// featured. Surfaced to the admin on the queue row.
    public string? Note { get; set; }

    public FeatureRequestStatus Status { get; set; } = FeatureRequestStatus.Pending;

    /// Set when an admin transitions the row out of Pending — captures
    /// who decided and when, plus an optional rationale shown back to
    /// the org (especially useful on Rejected).
    public Guid? ReviewedByUserId { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? DecisionNote { get; set; }

    public Report Report { get; set; } = null!;
    public Organization Organization { get; set; } = null!;
    public User RequestedByUser { get; set; } = null!;
    public User? ReviewedByUser { get; set; }
}
