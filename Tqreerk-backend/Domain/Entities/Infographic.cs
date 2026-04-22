using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

public class Infographic : SoftDeletableEntity
{
    public Guid ReportId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public string ChartType { get; set; } = string.Empty;

    /// <summary>jsonb — chart data payload</summary>
    public string? ChartData { get; set; }

    public string? ExportUrl { get; set; }

    public Report Report { get; set; } = null!;
    public User CreatedByUser { get; set; } = null!;
}
