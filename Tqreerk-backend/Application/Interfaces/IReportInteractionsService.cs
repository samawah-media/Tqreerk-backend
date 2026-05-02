using Taqreerk.Application.DTOs.Reports;

namespace Taqreerk.Application.Interfaces;

/// Per-user interactions on a published report: rate, save, recommend,
/// and record-a-view. All write operations are idempotent — calling
/// `Save` twice in a row leaves the user with one saved row, not two.
public interface IReportInteractionsService
{
    /// Upsert a 1..5 rating. Updates the report's running AvgRating /
    /// RatingsCount counters in the same transaction so reads stay
    /// consistent without a recompute job.
    Task<ReportInteractionStateDto> RateAsync(
        Guid userId, Guid reportId, RateReportRequest req, CancellationToken ct = default);

    /// Clear the caller's rating (no-op if they hadn't rated). Recomputes
    /// the report's aggregate counters on the way out.
    Task<ReportInteractionStateDto> UnrateAsync(
        Guid userId, Guid reportId, CancellationToken ct = default);

    /// Save the report to the user's "saved reports" list. Idempotent.
    Task<ReportInteractionStateDto> SaveAsync(
        Guid userId, Guid reportId, CancellationToken ct = default);

    /// Remove from "saved reports". No-op if not saved.
    Task<ReportInteractionStateDto> UnsaveAsync(
        Guid userId, Guid reportId, CancellationToken ct = default);

    /// Mark "I recommend this" — single row per (user, report) regardless
    /// of share-channel. The optional `shareChannel` annotates the row so
    /// later analytics can break recommendations down by surface.
    Task<ReportInteractionStateDto> RecommendAsync(
        Guid userId, Guid reportId, string? shareChannel, CancellationToken ct = default);

    Task<ReportInteractionStateDto> UnrecommendAsync(
        Guid userId, Guid reportId, CancellationToken ct = default);

    /// Record an anonymous (or authenticated) view. Per-IP+report dedupe
    /// in a 1-hour window so refresh-spam can't inflate the counter.
    Task RecordViewAsync(
        Guid reportId, Guid? userId, string? ipAddress, string? userAgent,
        CancellationToken ct = default);

    /// User-specific snapshot the public report page calls on mount when
    /// there's a token. Throws KeyNotFound when the report is missing or
    /// not published.
    Task<MyReportInteractionDto> GetMyStateAsync(
        Guid userId, Guid reportId, CancellationToken ct = default);
}
