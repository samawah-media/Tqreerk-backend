using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Points;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Common;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

/// See IPointsService. The service serializes mutations per-user with a
/// Postgres advisory lock so concurrent requests can't oversubscribe a
/// balance. The lock is transaction-scoped and keyed by userId.
public class PointsService : IPointsService
{
    private readonly TaqreerkDbContext _db;
    private readonly ILogger<PointsService> _logger;

    public PointsService(TaqreerkDbContext db, ILogger<PointsService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<int> EnsureRowAsync(Guid userId, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await AcquireLockAsync(userId, ct);

        var existing = await _db.UserPoints
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (existing is not null)
        {
            await tx.CommitAsync(ct);
            return existing.Balance;
        }

        var row = new UserPoints
        {
            UserId = userId,
            Balance = PointsConstants.WelcomeBalance,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.UserPoints.Add(row);

        _db.PointTransactions.Add(new PointTransaction
        {
            UserId = userId,
            Amount = PointsConstants.WelcomeBalance,
            BalanceAfter = PointsConstants.WelcomeBalance,
            Reason = "welcome_credit",
        });

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return row.Balance;
    }

    public async Task<int> GetBalanceAsync(Guid userId, CancellationToken ct = default)
    {
        var row = await _db.UserPoints
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
        // Lazy provision: if a legacy user somehow lacks a row, create
        // one rather than 500ing on the dashboard.
        return row?.Balance ?? await EnsureRowAsync(userId, ct);
    }

    public async Task<PointsBalanceDto> GetMyBalanceAsync(Guid userId, CancellationToken ct = default)
    {
        var row = await _db.UserPoints
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (row is null)
        {
            var balance = await EnsureRowAsync(userId, ct);
            return new PointsBalanceDto(balance, PointsConstants.WelcomeBalance, DateTime.UtcNow);
        }

        return new PointsBalanceDto(row.Balance, PointsConstants.WelcomeBalance, row.UpdatedAt);
    }

    public async Task<PointsHistoryPageDto> GetMyHistoryAsync(
        Guid userId, int page, int pageSize, CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        var query = _db.PointTransactions
            .AsNoTracking()
            .Where(t => t.UserId == userId);

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new PointTransactionDto(
                t.Id, t.Amount, t.BalanceAfter, t.Reason,
                t.ActionType, t.ResourceId, t.CreatedAt))
            .ToListAsync(ct);

        return new PointsHistoryPageDto(items, page, pageSize, total);
    }

    public Task<int> CreditAsync(
        Guid userId, int amount, string reason,
        UsageActionType? actionType = null, Guid? resourceId = null,
        CancellationToken ct = default)
    {
        if (amount <= 0)
            throw new ArgumentException("Credit amount must be positive.", nameof(amount));
        return ApplyDeltaAsync(userId, +amount, reason, actionType, resourceId, ct);
    }

    public Task<int> DebitAsync(
        Guid userId, int amount, string reason,
        UsageActionType? actionType = null, Guid? resourceId = null,
        CancellationToken ct = default)
    {
        if (amount <= 0)
            throw new ArgumentException("Debit amount must be positive.", nameof(amount));
        return ApplyDeltaAsync(userId, -amount, reason, actionType, resourceId, ct);
    }

    /// Single locked mutation path. Positive `delta` = credit, negative = debit.
    private async Task<int> ApplyDeltaAsync(
        Guid userId, int delta, string reason,
        UsageActionType? actionType, Guid? resourceId,
        CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await AcquireLockAsync(userId, ct);

        var row = await _db.UserPoints.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (row is null)
        {
            // Lazy-init under the lock so the welcome credit + this
            // delta land in the same balance line. EnsureRowAsync would
            // re-acquire the lock — inline the same body here instead.
            row = new UserPoints
            {
                UserId = userId,
                Balance = PointsConstants.WelcomeBalance,
                UpdatedAt = DateTime.UtcNow,
            };
            _db.UserPoints.Add(row);
            _db.PointTransactions.Add(new PointTransaction
            {
                UserId = userId,
                Amount = PointsConstants.WelcomeBalance,
                BalanceAfter = PointsConstants.WelcomeBalance,
                Reason = "welcome_credit",
            });
            await _db.SaveChangesAsync(ct);
        }

        var newBalance = row.Balance + delta;
        if (newBalance < 0)
        {
            await tx.RollbackAsync(ct);
            _logger.LogInformation(
                "[points] user={UserId} debit denied: balance={Balance} delta={Delta}",
                userId, row.Balance, delta);
            throw new InsufficientPointsException(row.Balance, -delta);
        }

        row.Balance = newBalance;
        row.UpdatedAt = DateTime.UtcNow;

        _db.PointTransactions.Add(new PointTransaction
        {
            UserId = userId,
            Amount = delta,
            BalanceAfter = newBalance,
            Reason = reason,
            ActionType = actionType,
            ResourceId = resourceId,
        });

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return newBalance;
    }

    /// Per-user advisory lock for the points balance. The second slot
    /// tags the lock so it doesn't collide with the usage-tracking lock
    /// keyspace (which uses 0xA0000000 | actionType).
    private async Task AcquireLockAsync(Guid userId, CancellationToken ct)
    {
        var slot1 = userId.GetHashCode();
        var slot2 = unchecked((int)0xB0000000);
        await _db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0}, {1})",
            new object[] { slot1, slot2 }, ct);
    }
}
