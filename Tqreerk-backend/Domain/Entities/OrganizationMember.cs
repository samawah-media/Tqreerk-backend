using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

public class OrganizationMember : BaseEntity
{
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

    public Organization Organization { get; set; } = null!;
    public User User { get; set; } = null!;
    public Role Role { get; set; } = null!;
}
