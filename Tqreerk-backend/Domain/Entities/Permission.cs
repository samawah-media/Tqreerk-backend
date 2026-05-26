using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

public class Permission : AuditableEntity
{
    public Guid PageId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string NameAr { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsSystem { get; set; }

    public Page Page { get; set; } = null!;
    public ICollection<RolePermission> RolePermissions { get; set; } = [];
}
