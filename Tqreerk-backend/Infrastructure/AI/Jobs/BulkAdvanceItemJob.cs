using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Taqreerk.Application.Common;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Settings;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Infrastructure.AI.Jobs;

/// <summary>
/// Hangfire job — Stages 2 and 3 of the bulk-import pipeline.
///
/// Polls the <c>ai_jobs</c> table for the GPU ingest job linked to the item.
/// When complete it dispatches <c>/bulk/summarize</c> and then polls for the
/// summarize job. On full completion it copies the summary into
/// <c>report_ai_contents</c>, publishes the report and marks the item
/// Completed.
///
/// "Not done yet" re-schedules a fresh invocation 10 s later so no retry
/// count is burned on normal polling. Actual errors (HTTP failures, DB
/// exceptions) use Hangfire's AutomaticRetry (5 attempts, exponential
/// back-off). On exhaustion <see cref="BulkJobFailedFilter"/> marks the item
/// Failed.
///
/// Idempotent: each state transition is guarded by a stage check so a
/// duplicate or re-queued invocation is a no-op.
/// </summary>
[Queue("bulk-advance")]
[AutomaticRetry(Attempts = 5,
    DelaysInSeconds = new[] { 30, 60, 120, 300, 600 },
    OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public class BulkAdvanceItemJob(
    TaqreerkDbContext db,
    IAiServiceClient ai,
    IOptions<FileStorageSettings> storageOpts,
    IBackgroundJobClient jobClient,
    ILogger<BulkAdvanceItemJob> logger)
{
    public async Task ExecuteAsync(Guid itemId, CancellationToken ct = default)
    {
        var item = await db.BulkImportItems
            .FirstOrDefaultAsync(i => i.Id == itemId, ct);

        if (item is null) return;

        switch (item.Stage)
        {
            case BulkImportItemStage.Completed:
            case BulkImportItemStage.Failed:
                return; // nothing to do
            case BulkImportItemStage.Pending:
            case BulkImportItemStage.Uploading:
                return; // upload job handles this
            case BulkImportItemStage.Ingesting:
                await HandleIngestingAsync(item, ct);
                break;
            case BulkImportItemStage.Summarizing:
                await HandleSummarizingAsync(item, ct);
                break;
        }
    }

    // ── Ingesting ─────────────────────────────────────────────────────────────

    private async Task HandleIngestingAsync(
        Domain.Entities.BulkImportItem item, CancellationToken ct)
    {
        // Resume path: no IngestJobId means chunks already exist → skip GPU.
        if (!item.IngestJobId.HasValue)
        {
            await DispatchSummarizeAsync(item, ct);
            return;
        }

        var aiJob = await db.AiJobs
            .Where(j => j.Id == item.IngestJobId.Value)
            .Select(j => new { j.Status, j.ErrorMessage, j.OutputData })
            .FirstOrDefaultAsync(ct);

        if (aiJob is null || aiJob.Status == AiJobStatus.Pending || aiJob.Status == AiJobStatus.Processing)
        {
            // Not done — reschedule without burning a retry.
            jobClient.Schedule<BulkAdvanceItemJob>(
                j => j.ExecuteAsync(item.Id, CancellationToken.None),
                TimeSpan.FromSeconds(10));
            return;
        }

        if (aiJob.Status == AiJobStatus.Failed)
        {
            // AI job is permanently failed — no point retrying; it won't
            // un-fail itself. Mark directly instead of throwing so we don't
            // burn all 5 Hangfire retries polling a dead ai_jobs row.
            MarkFailed(item, aiJob.ErrorMessage ?? "فشل استخراج محتوى التقرير.");
            await SaveAndUpdateCountersAsync(item, ct);
            return;
        }

        // Completed — update page count and chain to summarize.
        if (item.ReportId is { } rid)
        {
            var pages = BulkPipelineStatics.TryReadInt(aiJob.OutputData, "pages_processed");
            if (pages is not null)
                await db.Reports
                    .Where(r => r.Id == rid)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.PageCount, pages.Value), ct);
        }

        await DispatchSummarizeAsync(item, ct);
    }

    // ── Dispatch summarize ────────────────────────────────────────────────────

    private async Task DispatchSummarizeAsync(
        Domain.Entities.BulkImportItem item, CancellationToken ct)
    {
        if (item.ReportId is null)
        {
            MarkFailed(item, "لا يوجد تقرير مرتبط بهذا العنصر.");
            await SaveAndUpdateCountersAsync(item, ct);
            return;
        }

        var summarizeJobs = await ai.BulkSummarizeAsync(
            new List<Guid> { item.ReportId.Value }, ct);

        var sjId = summarizeJobs.FirstOrDefault(j => j.ReportId == item.ReportId.Value)?.JobId;
        if (sjId is null)
            throw new InvalidOperationException("AI service did not accept the summarize request.");

        item.SummarizeJobId = sjId;
        item.Stage          = BulkImportItemStage.Summarizing;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[bulk-advance] item={ItemId} → Summarizing summarizeJob={SummarizeJobId}",
            item.Id, sjId);

        // Immediately schedule the summarize poll — summarize is fast (30-60 s).
        jobClient.Schedule<BulkAdvanceItemJob>(
            j => j.ExecuteAsync(item.Id, CancellationToken.None),
            TimeSpan.FromSeconds(10));
    }

    // ── Summarizing ───────────────────────────────────────────────────────────

    private async Task HandleSummarizingAsync(
        Domain.Entities.BulkImportItem item, CancellationToken ct)
    {
        if (!item.SummarizeJobId.HasValue)
        {
            // Shouldn't happen — safety net.
            throw new InvalidOperationException("SummarizeJobId is missing for Summarizing item.");
        }

        var aiJob = await db.AiJobs
            .Where(j => j.Id == item.SummarizeJobId.Value)
            .Select(j => new { j.Status, j.ErrorMessage, j.OutputData })
            .FirstOrDefaultAsync(ct);

        if (aiJob is null || aiJob.Status == AiJobStatus.Pending || aiJob.Status == AiJobStatus.Processing)
        {
            jobClient.Schedule<BulkAdvanceItemJob>(
                j => j.ExecuteAsync(item.Id, CancellationToken.None),
                TimeSpan.FromSeconds(10));
            return;
        }

        if (aiJob.Status == AiJobStatus.Failed)
        {
            MarkFailed(item, aiJob.ErrorMessage ?? "فشل التلخيص.");
            await SaveAndUpdateCountersAsync(item, ct);
            return;
        }

        // Completed — persist summary and publish report.
        var report = await db.Reports.FirstOrDefaultAsync(r => r.Id == item.ReportId, ct);
        if (report is null)
        {
            MarkFailed(item, "اختفى صف التقرير بعد اكتمال التلخيص.");
            await SaveAndUpdateCountersAsync(item, ct);
            return;
        }

        // Guard against duplicate inserts when two instances race on the same report.
        try
        {
            await BulkPipelineStatics.CopySummaryToContentAsync(
                db, report, item.SummarizeJobId.Value, aiJob.OutputData, ct);
            report.Status      = ReportStatus.Published;
            report.PublishedAt ??= DateTime.UtcNow;
            item.Stage         = BulkImportItemStage.Completed;
            item.CompletedAt   = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: "23505",
                ConstraintName: "IX_report_ai_contents_ReportId_Language" })
        {
            foreach (var entry in db.ChangeTracker.Entries<Domain.Entities.ReportAiContent>()
                         .Where(e => e.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;

            report.Status      = ReportStatus.Published;
            report.PublishedAt ??= DateTime.UtcNow;
            item.Stage         = BulkImportItemStage.Completed;
            item.CompletedAt   = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        await db.BulkImportJobs
            .Where(j => j.Id == item.JobId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.CompletedCount, j => j.CompletedCount + 1), ct);

        logger.LogInformation(
            "[bulk-advance] item={ItemId} → Completed report={ReportId}", item.Id, item.ReportId);

        await TryFinaliseJobAsync(item.JobId, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void MarkFailed(Domain.Entities.BulkImportItem item, string error)
    {
        item.Stage        = BulkImportItemStage.Failed;
        item.ErrorMessage = error.Length > 4000 ? error[..4000] : error;
        item.CompletedAt  = DateTime.UtcNow;
    }

    private async Task SaveAndUpdateCountersAsync(
        Domain.Entities.BulkImportItem item, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct);
        if (item.Stage == BulkImportItemStage.Failed)
            await db.BulkImportJobs
                .Where(j => j.Id == item.JobId)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.FailedCount, j => j.FailedCount + 1), ct);
        await TryFinaliseJobAsync(item.JobId, ct);
    }

    private async Task TryFinaliseJobAsync(Guid jobId, CancellationToken ct)
    {
        var anyActive = await db.BulkImportItems.AnyAsync(i =>
            i.JobId == jobId
            && i.Stage != BulkImportItemStage.Completed
            && i.Stage != BulkImportItemStage.Failed, ct);
        if (anyActive) return;

        await db.BulkImportJobs
            .Where(j => j.Id == jobId && j.Status == BulkImportStatus.Processing)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, BulkImportStatus.Completed)
                .SetProperty(j => j.CompletedAt, DateTime.UtcNow), ct);
        BulkImportKeywordsCache.ClearJob(jobId);
        logger.LogInformation("[bulk-advance] job={JobId} finished", jobId);
    }
}
