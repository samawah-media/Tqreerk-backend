using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

public class ReportRating : BaseEntity
{
    public Guid ReportId { get; set; }
    public Guid UserId { get; set; }
    public int Rating { get; set; }
    public string? Review { get; set; }

    public Report Report { get; set; } = null!;
    public User User { get; set; } = null!;
}
