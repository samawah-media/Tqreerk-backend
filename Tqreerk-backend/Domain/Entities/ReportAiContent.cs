using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

public class ReportAiContent : SoftDeletableEntity
{
    public Guid ReportId { get; set; }
    public Guid? AiJobId { get; set; }
    public string? Summary { get; set; }
    public string Language { get; set; } = "ar";

    /// <summary>jsonb</summary>
    public string? KeyFindings { get; set; }

    /// <summary>jsonb</summary>
    public string? Recommendations { get; set; }

    /// <summary>jsonb</summary>
    public string? Indicators { get; set; }

    /// <summary>jsonb</summary>
    public string? Trends { get; set; }

    public DateTime? GeneratedAt { get; set; }

    public Report Report { get; set; } = null!;
    public AiJob? AiJob { get; set; }
}
