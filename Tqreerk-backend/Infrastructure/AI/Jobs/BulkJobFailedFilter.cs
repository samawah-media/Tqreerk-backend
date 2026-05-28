using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Infrastructure.AI.Jobs;

/// <summary>
/// Hangfire job filter that fires when a bulk-pipeline job exhausts all
/// automatic retries (transitions to <see cref="DeletedState"/>). It marks
/// the corresponding <see cref="Domain.Entities.BulkImportItem"/> as Failed
/// and updates the job-level FailedCount so the admin progress UI reflects
/// the outcome without polling.
/// </summary>
public sealed class BulkJobFailedFilter(IServiceProvider services)
    : JobFilterAttribute, IElectStateFilter
{
    public void OnStateElection(ElectStateContext context)
    {
        if (context.CandidateState is not DeletedState) return;

        Guid? itemId = null;

        // Both job types carry itemId as the first argument.
        var args = context.BackgroundJob.Job.Args;
        if (args is { Count: > 0 } && args[0] is Guid g)
            itemId = g;

        if (itemId is null) return;

        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaqreerkDbContext>();

            var updated = db.BulkImportItems
                .Where(i => i.Id == itemId.Value
                         && i.Stage != BulkImportItemStage.Completed
                         && i.Stage != BulkImportItemStage.Failed)
                .ExecuteUpdate(s => s
                    .SetProperty(i => i.Stage, BulkImportItemStage.Failed)
                    .SetProperty(i => i.ErrorMessage, "فشل المعالجة بعد عدة محاولات — راجع سجل Hangfire.")
                    .SetProperty(i => i.CompletedAt, DateTime.UtcNow));

            if (updated > 0)
            {
                db.BulkImportJobs
                    .Where(j => j.Items.Any(i => i.Id == itemId.Value))
                    .ExecuteUpdate(s => s.SetProperty(j => j.FailedCount, j => j.FailedCount + 1));
            }
        }
        catch
        {
            // Never crash Hangfire state machine on filter failure.
        }
    }
}
