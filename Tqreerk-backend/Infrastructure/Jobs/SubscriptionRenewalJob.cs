using Hangfire;
using Taqreerk.Application.Services;

namespace Taqreerk.Infrastructure.Jobs;

/// <summary>
/// Daily Hangfire job: charge Moyasar card tokens for subscriptions nearing EndDate.
/// </summary>
[Queue("default")]
[AutomaticRetry(Attempts = 1, DelaysInSeconds = new[] { 300 })]
public sealed class SubscriptionRenewalJob(SubscriptionRenewalService renewals)
{
    public Task ExecuteAsync(CancellationToken ct = default)
        => renewals.ProcessDueRenewalsAsync(ct);
}
