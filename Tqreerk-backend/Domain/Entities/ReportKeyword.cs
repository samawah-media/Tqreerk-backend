using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

public class ReportKeyword : BaseEntity
{
    public Guid ReportId { get; set; }
    public string Keyword { get; set; } = string.Empty;
    public string Language { get; set; } = "ar";

    public Report Report { get; set; } = null!;
}
