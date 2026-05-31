using Microsoft.Extensions.Logging;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

/// <summary>
/// Hangfire-driven batch: expire paid subscriptions when EndDate passes (no grace).
/// </summary>
public sealed class SubscriptionExpirationService
{
    private readonly TaqreerkDbContext _db;
    private readonly ILogger<SubscriptionExpirationService> _logger;

    public SubscriptionExpirationService(
        TaqreerkDbContext db,
        ILogger<SubscriptionExpirationService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task<SubscriptionLifecycleService.ExpirationBatchResult> ProcessExpiredSubscriptionsAsync(
        CancellationToken ct = default)
        => SubscriptionLifecycleService.ProcessAllExpiredSubscriptionsAsync(_db, _logger, ct);

    public Task ApplyForUserAsync(Guid userId, CancellationToken ct = default)
        => SubscriptionLifecycleService.ApplyExpirationTransitionsForUserAsync(_db, userId, ct);
}
