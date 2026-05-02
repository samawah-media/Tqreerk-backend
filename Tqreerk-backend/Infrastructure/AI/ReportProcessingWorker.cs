using Microsoft.Extensions.Options;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Settings;

namespace Taqreerk.Infrastructure.AI;

/// <summary>
/// Reconciles .NET-owned report state with what the AI service has finished.
/// We don't process ai_jobs rows here anymore — the AI service's worker
/// (pipelines/jobs.py) drains the table end-to-end. This service used to do
/// the same, which caused a race: both workers claimed the same row and the
/// AI side rejected our .NET-only Summarization rows with "Unknown JobType".
///
/// What's left is a status-finalizer pass: every tick we look for ai_jobs that
/// just transitioned to terminal status and update the matching reports.Status
/// (ProcessingAi → Published / Approved on failure) plus any in-flight
/// ReportTranslation rows.
/// </summary>
public class ReportProcessingWorker : BackgroundService
{
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
            "ReportProcessingWorker started (finalizer mode) — polling every {Seconds}s",
            pollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var ai = scope.ServiceProvider.GetRequiredService<IReportAiService>();
                await ai.FinalizeCompletedJobsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                // The worker MUST never die. Any unhandled error is logged and
                // we keep ticking so transient blips don't strand reports
                // forever in ProcessingAi.
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
}
