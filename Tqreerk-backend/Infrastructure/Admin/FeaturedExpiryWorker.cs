using Microsoft.EntityFrameworkCore;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Infrastructure.Admin;

/// Deactivates featured rows whose FeaturedUntil has passed. Cheap loop —
/// one indexed UPDATE per minute touching only the rows that just expired.
///
/// Same single-instance + log-and-continue policy as ClaimAutoReleaseWorker.
/// Adding a second replica would re-flag the same rows but the second pass
/// is a no-op (IsActive is already false), so it's idempotent rather than
/// strictly racy.
public class FeaturedExpiryWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _services;
    private readonly ILogger<FeaturedExpiryWorker> _logger;

    public FeaturedExpiryWorker(IServiceProvider services, ILogger<FeaturedExpiryWorker> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "FeaturedExpiryWorker started — polling every {Seconds}s",
            PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FeaturedExpiryWorker tick failed");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("FeaturedExpiryWorker stopped");
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaqreerkDbContext>();

        var now = DateTime.UtcNow;
        var expired = await db.FeaturedReports
            .Where(f => f.IsActive
                     && f.FeaturedUntil != null
                     && f.FeaturedUntil <= now)
            .ToListAsync(ct);

        if (expired.Count == 0) return;

        foreach (var f in expired) f.IsActive = false;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "FeaturedExpiryWorker deactivated {Count} expired featured row(s)",
            expired.Count);
    }
}
