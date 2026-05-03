using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

/// Single free-text notepad per (user, report). Distinct from highlight
/// notes: highlights are anchored to a text selection on a page, this
/// is a top-level "my thoughts on this report" pad. The unique index on
/// (UserId, ReportId) enforces the one-per-pair invariant.
public class ReportPersonalNote : AuditableEntity
{
    public Guid UserId { get; set; }
    public Guid ReportId { get; set; }

    /// Free-form Markdown / plain text. The editor's notepad drawer
    /// debounces writes through PUT /api/me/reports/{id}/note.
    public string Body { get; set; } = string.Empty;

    public User User { get; set; } = null!;
    public Report Report { get; set; } = null!;
}
