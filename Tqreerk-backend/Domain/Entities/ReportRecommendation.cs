using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

public class ReportRecommendation : BaseEntity
{
    public Guid ReportId { get; set; }
    public Guid UserId { get; set; }
    public string? ShareChannel { get; set; }

    public Report Report { get; set; } = null!;
    public User User { get; set; } = null!;
}
