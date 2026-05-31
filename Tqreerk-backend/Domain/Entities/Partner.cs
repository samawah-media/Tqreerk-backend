using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

public class Partner : BaseEntity
{
    public Guid CategoryId { get; set; }
    public PartnerCategory Category { get; set; } = null!;

    public string NameAr { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string? LogoUrl { get; set; }
    public string? WebsiteUrl { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}
