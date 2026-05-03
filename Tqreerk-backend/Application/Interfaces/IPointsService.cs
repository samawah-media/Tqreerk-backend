using Taqreerk.Application.DTOs.Points;
using Taqreerk.Domain.Enums;

namespace Taqreerk.Application.Interfaces;

/// Per-user point currency. Decoupled from IUsageService: a points debit
/// can succeed under the freemium gate or vice versa. Until we merge the
/// two, treat them as independent gates.
///
/// Concurrency: every mutation (CreditAsync, DebitAsync, EnsureRowAsync)
/// serializes through a Postgres advisory lock keyed by userId, so
/// concurrent debits cannot drive the balance negative.
public interface IPointsService
{
    /// Lazy-init: creates a `user_points` row with `WelcomeBalance` and
    /// records the welcome transaction. Idempotent — safe to call from
    /// the registration path AND from the migration backfill, returns
    /// the existing balance if the row already exists.
    Task<int> EnsureRowAsync(Guid userId, CancellationToken ct = default);

    Task<int> GetBalanceAsync(Guid userId, CancellationToken ct = default);

    Task<PointsBalanceDto> GetMyBalanceAsync(Guid userId, CancellationToken ct = default);

    Task<PointsHistoryPageDto> GetMyHistoryAsync(
        Guid userId, int page, int pageSize, CancellationToken ct = default);

    /// Add `amount` (must be positive) and write a transaction. Returns
    /// the new balance.
    Task<int> CreditAsync(
        Guid userId, int amount, string reason,
        UsageActionType? actionType = null, Guid? resourceId = null,
        CancellationToken ct = default);

    /// Subtract `amount` (must be positive) and write a transaction.
    /// Throws InsufficientPointsException if balance < amount. Returns
    /// the new balance.
    Task<int> DebitAsync(
        Guid userId, int amount, string reason,
        UsageActionType? actionType = null, Guid? resourceId = null,
        CancellationToken ct = default);
}

public sealed class InsufficientPointsException : Exception
{
    public int Balance { get; }
    public int Required { get; }

    public InsufficientPointsException(int balance, int required)
        : base($"Insufficient points: balance {balance}, need {required}.")
    {
        Balance = balance;
        Required = required;
    }
}
