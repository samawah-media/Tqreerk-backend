using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Settings;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Infrastructure.AI;

/// Polls the ai_jobs table for Pending rows, processes them one at a time
/// per tick. Single-instance — fine for staging on Cloud Run min=max=1. When
/// we need to scale, switch to a queue (Hangfire / Pub/Sub) and remove this.
public class ReportProcessingWorker : BackgroundService
{
    private const int BatchSize = 5;

    private readonly IServiceProvider _services;
    private readonly AiServiceSettings _settings;
    private readonly ILogger<ReportProcessingWorker> _logger;

    public ReportProcessingWorker(
        IServiceProvider services,
        IOptions<AiServiceSettings> settings,
        ILogger<ReportProcessingWorker> logger)
    {
        _services = services;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollInterval = TimeSpan.FromSeconds(Math.Max(1, _settings.WorkerPollSeconds));
        _logger.LogInformation(
            "ReportProcessingWorker started — polling every {Seconds}s, batch size {Batch}",
            pollInterval.TotalSeconds, BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                // The worker MUST never die. Any unhandled error is a bug we want
                // surfaced (Sentry would catch it via the global handler if this
                // were a request) but we log+continue so the queue keeps draining.
                _logger.LogError(ex, "ReportProcessingWorker tick failed unexpectedly");
            }

            try
            {
                await Task.Delay(pollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("ReportProcessingWorker stopped");
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // New scope per tick — DbContext is scoped, and we may take seconds-to-minutes
        // per job, so we don't want to hold a single context across the full loop.
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaqreerkDbContext>();

        var pendingIds = await db.AiJobs
            .Where(j => j.Status == AiJobStatus.Pending)
            .OrderBy(j => j.CreatedAt)
            .Select(j => j.Id)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (pendingIds.Count == 0) return;

        var ai = scope.ServiceProvider.GetRequiredService<IReportAiService>();
        foreach (var jobId in pendingIds)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await ai.ProcessJobAsync(jobId, ct);
            }
            catch (Exception ex)
            {
                // ReportAiService is supposed to swallow per-job errors and mark
                // the job Failed. If something escapes, log it so the row stays
                // Processing — the user can re-trigger via the regenerate endpoint.
                _logger.LogError(ex, "Unhandled error processing AiJob {JobId}", jobId);
            }
        }
    }
}
