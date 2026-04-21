using Taqreerk.Domain.Common;
using Taqreerk.Domain.Enums;

namespace Taqreerk.Domain.Entities;

public class ReportTranslation : SoftDeletableEntity
{
    public Guid ReportId { get; set; }
    public Guid? AiJobId { get; set; }
    public string Language { get; set; } = string.Empty;
    public string? TranslatedTitle { get; set; }
    public string? TranslatedDescription { get; set; }
    public string? TranslatedSummary { get; set; }
    public TranslationStatus TranslationStatus { get; set; } = TranslationStatus.Pending;
    public int TokensUsed { get; set; }
    public DateTime? TranslatedAt { get; set; }

    public Report Report { get; set; } = null!;
    public AiJob? AiJob { get; set; }
}
