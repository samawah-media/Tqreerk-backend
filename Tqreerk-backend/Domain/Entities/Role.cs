using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

public class Role : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>jsonb — permission keys granted to this role</summary>
    public string? Permissions { get; set; }

    public ICollection<OrganizationMember> OrganizationMembers { get; set; } = [];
}
