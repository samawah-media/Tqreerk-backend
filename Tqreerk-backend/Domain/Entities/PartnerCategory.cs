using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

public class PartnerCategory : BaseEntity
{
    public string NameAr { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    public ICollection<Partner> Partners { get; set; } = [];
}
