using Taqreerk.Domain.Common;
using Taqreerk.Domain.Enums;

namespace Taqreerk.Domain.Entities;

public class AiJob : BaseEntity
{
    public Guid? OrganizationId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? ReportId { get; set; }
    public AiJobType JobType { get; set; }
    public AiJobStatus Status { get; set; } = AiJobStatus.Pending;
    public int TokensUsed { get; set; }

    /// <summary>jsonb</summary>
    public string? InputData { get; set; }

    /// <summary>jsonb</summary>
    public string? OutputData { get; set; }

    public string? ErrorMessage { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public Organization? Organization { get; set; }
    public User? User { get; set; }
    public Report? Report { get; set; }
    public ICollection<ReportAiContent> AiContents { get; set; } = [];
    public ICollection<ReportTranslation> Translations { get; set; } = [];
    public ICollection<ReportComparison> Comparisons { get; set; } = [];
}
