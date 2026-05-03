using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

/// Per-user points balance — one row per user. Updates flow through
/// IPointsService which serializes them with a Postgres advisory lock so
/// concurrent debits can't drive the balance negative. The transaction
/// log is in PointTransaction (one row per credit/debit).
public class UserPoints : BaseEntity
{
    /// 1:1 with users.Id — also the partition key for advisory locks.
    public Guid UserId { get; set; }

    /// Current balance. Never negative — debits past the balance throw
    /// InsufficientPointsException.
    public int Balance { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
}
