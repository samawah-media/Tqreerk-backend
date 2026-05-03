using Taqreerk.Domain.Common;
using Taqreerk.Domain.Enums;

namespace Taqreerk.Domain.Entities;

/// Append-only ledger of every credit/debit on a user's points balance.
/// Credits are positive amounts; debits are negative. `BalanceAfter` is
/// denormalized so we can render history without summing the whole table
/// per row.
///
/// `ActionType` and `ResourceId` are populated when the transaction was
/// driven by a metered action (e.g. AiTranslate on report X). Free-form
/// adjustments (welcome credit, refund, admin grant) leave them null and
/// describe the reason in the human-readable `Reason` string.
public class PointTransaction : BaseEntity
{
    public Guid UserId { get; set; }

    /// + for credits, - for debits.
    public int Amount { get; set; }

    /// Snapshot of `user_points.balance` AFTER this row was applied.
    public int BalanceAfter { get; set; }

    public string Reason { get; set; } = string.Empty;

    /// When the transaction was driven by a metered action.
    public UsageActionType? ActionType { get; set; }

    /// Usually the report id the action targeted.
    public Guid? ResourceId { get; set; }

    public User User { get; set; } = null!;
}
