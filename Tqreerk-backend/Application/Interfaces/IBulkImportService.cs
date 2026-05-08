using Taqreerk.Application.DTOs.Admin;

namespace Taqreerk.Application.Interfaces;

/// <summary>
/// Admin-only "import a batch of third-party reports" feature. The admin
/// uploads one Excel sheet describing up to 10 reports + their public
/// PDF URLs; we fetch each PDF, store it, create the Report rows, and
/// drive them through ingest → summarize → Published using the AI service's
/// /bulk endpoints.
///
/// All endpoints below short-circuit at parse / validation; the actual
/// fetch + AI work runs in <c>BulkImportProcessor</c> off the request
/// thread so the admin gets a job_id back immediately and polls for
/// progress.
/// </summary>
public interface IBulkImportService
{
    /// Validate + persist the parsed Excel as a Pending job. Returns the
    /// new job id; the background processor picks it up on its next tick
    /// and starts pumping rows through the pipeline.
    Task<Guid> CreateJobAsync(
        Guid adminUserId,
        Stream excelStream,
        string fileName,
        CancellationToken ct = default);

    /// Get one job + its items (per-row stages) for the live progress UI.
    Task<BulkImportJobDetailDto?> GetJobAsync(Guid jobId, CancellationToken ct = default);

    /// List the calling admin's recent bulk imports, newest first. Lean
    /// payload — items are NOT included; the detail endpoint loads them
    /// on demand.
    Task<IReadOnlyList<BulkImportJobSummaryDto>> ListJobsAsync(
        Guid adminUserId, int take = 20, CancellationToken ct = default);

    /// Build the empty .xlsx template the admin downloads to fill in.
    /// Headers match the parser exactly so a fully-typed cycle (download
    /// → fill → upload) round-trips losslessly.
    byte[] GenerateTemplate();

    /// <summary>
    /// Reset every Failed item on the job back to Pending so the
    /// background processor picks them up again on its next tick. The
    /// item's stage-specific cursors (IngestJobId / SummarizeJobId) are
    /// cleared so the pipeline starts each row from whatever stage makes
    /// sense for its current state — pre-upload failures retry the whole
    /// fetch+upload+ingest, mid-pipeline failures pick up where they
    /// stopped (chunks already in DB → straight to summarize).
    ///
    /// Returns the number of items that were eligible for retry. Zero is
    /// not an error; the controller maps it to a no-op 200.
    /// </summary>
    Task<int> RetryFailedAsync(Guid jobId, CancellationToken ct = default);
}
