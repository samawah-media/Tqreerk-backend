using Hangfire;
using Taqreerk.Application.Services;

namespace Taqreerk.Infrastructure.Jobs;

/// <summary>
/// Daily Hangfire job: downgrade expired individual plans to free and mark org subscriptions expired.
/// Runs hourly; expiration is exact at EndDate (no grace).
/// </summary>
[Queue("default")]
[AutomaticRetry(Attempts = 2, DelaysInSeconds = new[] { 120, 600 })]
public sealed class SubscriptionExpirationJob(SubscriptionExpirationService expiration)
{
    public Task ExecuteAsync(CancellationToken ct = default)
        => expiration.ProcessExpiredSubscriptionsAsync(ct);
}
