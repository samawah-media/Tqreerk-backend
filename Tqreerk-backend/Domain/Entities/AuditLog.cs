using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

public class AuditLog : BaseEntity
{
    public Guid? OrganizationId { get; set; }
    public Guid? UserId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public string? IpAddress { get; set; }

    /// <summary>jsonb — change payload</summary>
    public string? Payload { get; set; }

    public Organization? Organization { get; set; }
}
