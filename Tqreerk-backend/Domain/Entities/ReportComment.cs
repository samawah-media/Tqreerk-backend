using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

/// One comment by an authenticated user on a published report. Single
/// level — no replies/threading in this iteration. Soft-deletable so the
/// public page can hide a removed comment without losing the row (helps
/// keep `RatingsCount`-style aggregates stable if we ever add one for
/// comments).
public class ReportComment : SoftDeletableEntity
{
    public Guid ReportId { get; set; }
    public Guid UserId { get; set; }

    /// Free-text body. Length-bounded at the EF config layer (4 KB) so a
    /// runaway payload can't bloat the row. Trim happens at the service.
    public string Body { get; set; } = string.Empty;

    public Report Report { get; set; } = null!;
    public User User { get; set; } = null!;
}
