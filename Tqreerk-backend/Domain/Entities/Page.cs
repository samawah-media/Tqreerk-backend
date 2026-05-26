using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

public class Page : AuditableEntity
{
    public string Key { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public bool IsSystem { get; set; }

    public ICollection<Permission> Permissions { get; set; } = [];
}
