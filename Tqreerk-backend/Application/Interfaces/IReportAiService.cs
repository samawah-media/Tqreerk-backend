using Taqreerk.Application.DTOs.Reports;

namespace Taqreerk.Application.Interfaces;

/// Owns the AI processing pipeline for a Report:
///   Approve → Ingest+Summarize (auto, runs in AI service) → admin enables
///   translation per-org → user clicks Translate → Translate (manual).
///
/// The .NET side is the orchestrator: it writes ai_jobs rows that the AI
/// service's worker picks up (via FOR UPDATE SKIP LOCKED on the same table).
/// The actual page extraction, summary generation, and document translation
/// all run inside the Python ai-service. This service no longer processes
/// jobs itself — the previous in-process worker conflicted with the AI
/// service's worker on the shared queue.
public interface IReportAiService
{
    /// Kick off the pipeline: creates a single Ingestion job with the
    /// "ingest+summarize" step so the AI worker handles both stages in one
    /// pass. Idempotent — skips when a Pending/Processing ingest already
    /// exists for the report.
    Task EnqueueIngestAsync(Guid reportId, CancellationToken ct = default);

    /// Manually trigger a translation job. Gated by the org's
    /// TranslationEnabled flag and by ingest having already produced an
    /// Arabic summary. Throws UnauthorizedAccessException if the org isn't
    /// allowed to translate, KeyNotFoundException if the report doesn't
    /// exist, InvalidOperationException for missing prerequisites.
    Task EnqueueTranslateAsync(Guid currentUserId, Guid reportId, CancellationToken ct = default);

    /// Resets any Failed jobs for the report back to Pending so the AI
    /// worker picks them up again. Used by the "إعادة المعالجة" button.
    Task RegenerateAsync(Guid currentUserId, Guid reportId, CancellationToken ct = default);

    /// Returns a snapshot of where the report is in the pipeline — useful
    /// for the frontend's polling badge.
    Task<ReportAiStatusDto> GetStatusAsync(Guid currentUserId, Guid reportId, CancellationToken ct = default);

    /// Background-service hook: scans for Ingestion jobs that the AI service
    /// just flipped to Completed and finalises the .NET-owned Report.Status
    /// (ProcessingAi → Published). The AI worker doesn't know our report
    /// state machine, so this side reconciles.
    Task FinalizeCompletedJobsAsync(CancellationToken ct = default);
}
