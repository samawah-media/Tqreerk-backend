using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

/// Free-text note on a saved report. Each user can have many notes per
/// report; the editor exposes a list with add/edit/delete. Distinct from
/// highlight notes (those are anchored to a text selection on a page).
public class ReportPersonalNote : AuditableEntity
{
    public Guid UserId { get; set; }
    public Guid ReportId { get; set; }

    public string Body { get; set; } = string.Empty;

    public User User { get; set; } = null!;
    public Report Report { get; set; } = null!;
}
