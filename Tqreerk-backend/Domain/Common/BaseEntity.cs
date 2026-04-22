namespace Taqreerk.Domain.Common;

public abstract class BaseEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public abstract class AuditableEntity : BaseEntity
{
    public DateTime? UpdatedAt { get; set; }
}

public abstract class SoftDeletableEntity : AuditableEntity
{
    public DateTime? DeletedAt { get; set; }
}
