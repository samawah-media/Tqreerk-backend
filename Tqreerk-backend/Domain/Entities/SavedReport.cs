using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

public class SavedReport : BaseEntity
{
    public Guid UserId { get; set; }
    public Guid ReportId { get; set; }
    public DateTime SavedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
    public Report Report { get; set; } = null!;
}
