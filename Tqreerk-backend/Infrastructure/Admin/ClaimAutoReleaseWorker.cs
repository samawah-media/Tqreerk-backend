using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Settings;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Infrastructure.Admin;

/// Sweeps the reports table for stale UnderReview claims (claimed >N
/// minutes ago) and releases them back to the queue. Reviewers who close
/// their tab or get distracted shouldn't lock a report indefinitely —
/// the plan calls this out as a 60-minute auto-release.
///
/// Single-instance just like the AI worker. Two replicas would race on
/// the SaveChanges; a row-version column would let them coexist but it's
/// cheaper to just keep min=max=1 in Cloud Run.
public class ClaimAutoReleaseWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly AdminWorkerSettings _settings;
    private readonly ILogger<ClaimAutoReleaseWorker> _logger;

    public ClaimAutoReleaseWorker(
        IServiceProvider services,
        IOptions<AdminWorkerSettings> settings,
        ILogger<ClaimAutoReleaseWorker> logger)
    {
        _services = services;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollInterval = TimeSpan.FromSeconds(Math.Max(15, _settings.ClaimSweepPollSeconds));
        var maxAge = TimeSpan.FromMinutes(Math.Max(5, _settings.ClaimMaxAgeMinutes));

        _logger.LogInformation(
            "ClaimAutoReleaseWorker started — polling every {Seconds}s; releasing claims older than {Age} minutes",
            pollInterval.TotalSeconds, maxAge.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(maxAge, stoppingToken); }
            catch (Exception ex)
            {
                // Mirror the AI worker's policy: log loudly, never die.
                _logger.LogError(ex, "ClaimAutoReleaseWorker tick failed");
            }

            try { await Task.Delay(pollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("ClaimAutoReleaseWorker stopped");
    }

    private async Task TickAsync(TimeSpan maxAge, CancellationToken ct)
    {
        // Per-tick scope so the DbContext + audit logger get fresh
        // per-request-style lifetimes. The auto-release isn't a request
        // but the scoping makes the DI math simpler.
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaqreerkDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAdminActionLogger>();

        var cutoff = DateTime.UtcNow - maxAge;

        // Find stale claims. We do this in two steps — read ids/metadata
        // first so we can write per-row audit entries, then a single
        // ExecuteUpdate to reset the columns. That's safer than a bulk
        // update that loses the original ClaimedAt for the audit row.
        var stale = await db.Reports
            .AsNoTracking()
            .Where(r =>
                r.Status == ReportStatus.UnderReview
                && r.ClaimedByReviewerId != null
                && r.ClaimedAt != null
                && r.ClaimedAt < cutoff)
            .Select(r => new
            {
                r.Id,
                r.Title,
                ReviewerId = r.ClaimedByReviewerId!.Value,
                ClaimedAt = r.ClaimedAt!.Value,
            })
            .Take(50)
            .ToListAsync(ct);

        if (stale.Count == 0) return;

        _logger.LogInformation(
            "Auto-releasing {Count} stale claim(s) (older than {Cutoff:o})",
            stale.Count, cutoff);

        foreach (var r in stale)
        {
            if (ct.IsCancellationRequested) break;

            // Single-row UPDATE with status guard so we don't fight a
            // reviewer who's actively submitting a decision (their
            // SaveChanges and ours would otherwise race).
            var rows = await db.Reports
                .Where(x =>
                    x.Id == r.Id
                    && x.Status == ReportStatus.UnderReview
                    && x.ClaimedByReviewerId == r.ReviewerId
                    && x.ClaimedAt < cutoff)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, ReportStatus.PendingReview)
                    .SetProperty(x => x.ClaimedByReviewerId, (Guid?)null)
                    .SetProperty(x => x.ClaimedAt, (DateTime?)null),
                    ct);

            if (rows == 0)
            {
                // Lost the race — reviewer just submitted a decision or
                // released manually. Skip the audit row; their action
                // will have already produced one of its own.
                continue;
            }

            await audit.LogAsync(
                adminUserId: null, // system action — no admin actor
                actionType: "report.claim_auto_released",
                targetEntityType: "report",
                targetEntityId: r.Id,
                reason: $"Claim held by reviewer {r.ReviewerId} since {r.ClaimedAt:o} exceeded {(int)maxAge.TotalMinutes}-minute TTL.",
                beforeState: new { reviewerId = r.ReviewerId, claimedAt = r.ClaimedAt },
                afterState: new { status = ReportStatus.PendingReview.ToString() },
                ct: ct);
        }
    }
}
