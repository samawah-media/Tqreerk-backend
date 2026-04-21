using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

public class ReportComparison : BaseEntity
{
    public Guid UserId { get; set; }

    /// <summary>jsonb — array of report UUIDs being compared</summary>
    public string ReportIds { get; set; } = "[]";

    public Guid? AiJobId { get; set; }
    public string? ComparisonResult { get; set; }
    public decimal? SimilarityScore { get; set; }

    public User User { get; set; } = null!;
    public AiJob? AiJob { get; set; }
}
