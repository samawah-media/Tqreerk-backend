using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

public class ReportView : BaseEntity
{
    public Guid ReportId { get; set; }
    public Guid? UserId { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTime ViewedAt { get; set; } = DateTime.UtcNow;

    public Report Report { get; set; } = null!;
    public User? User { get; set; }
}
