using Taqreerk.Application.DTOs.Reports;

namespace Taqreerk.Application.Interfaces;

/// Owns the AI processing pipeline for a Report:
///   Ingest → Summarize (ar) → Translate (en) → Publish.
/// Public API is just "enqueue" + "status" + "regenerate"; the actual work runs
/// in ReportProcessingWorker which pumps the ai_jobs table.
public interface IReportAiService
{
    /// Kick off the pipeline: creates an Ingest job for the report. Idempotent —
    /// if a Pending/Processing job already exists, returns the existing one.
    Task EnqueueIngestAsync(Guid reportId, CancellationToken ct = default);

    /// Resets any Failed jobs for the report back to Pending so the worker
    /// picks them up again. Used by the "إعادة المعالجة" button.
    Task RegenerateAsync(Guid currentUserId, Guid reportId, CancellationToken ct = default);

    /// Returns a snapshot of where the report is in the pipeline — useful for
    /// the frontend's polling badge.
    Task<ReportAiStatusDto> GetStatusAsync(Guid currentUserId, Guid reportId, CancellationToken ct = default);

    /// Worker-facing: process a single AiJob end-to-end. Routes by JobType and
    /// updates job status + chains the next job on success. Catches all
    /// exceptions and stores them on the job so the loop never crashes the
    /// background service.
    Task ProcessJobAsync(Guid jobId, CancellationToken ct = default);
}
