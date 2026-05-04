using Taqreerk.Application.DTOs.FeatureRequests;
using Taqreerk.Domain.Enums;

namespace Taqreerk.Application.Interfaces;

/// Org-side and admin-side feature-request flow. Lives in one service
/// because both sides operate on the same `report_feature_requests`
/// rows; splitting them would mean two services dancing around the
/// same DbSet for no real isolation gain.
public interface IFeatureRequestsService
{
    // ── Org-side ─────────────────────────────────────────────────────

    /// Create a Pending request for the given report. The caller must
    /// belong to the report's owning org and the report must be Published.
    /// Throws InvalidOperationException (=409) when a Pending row
    /// already exists for the report.
    Task<FeatureRequestDto> CreateAsync(
        Guid currentUserId, Guid reportId, CreateFeatureRequest req, CancellationToken ct = default);

    /// Get the most recent feature request for a report (any status).
    /// Returns null when the report has never been submitted. Caller
    /// must own the report.
    Task<FeatureRequestDto?> GetForReportAsync(
        Guid currentUserId, Guid reportId, CancellationToken ct = default);

    /// All feature requests submitted by the caller's org, optionally
    /// filtered by status. Newest first.
    Task<IReadOnlyList<FeatureRequestDto>> ListForOrgAsync(
        Guid currentUserId, FeatureRequestStatus? status, CancellationToken ct = default);

    // ── Admin-side ───────────────────────────────────────────────────

    /// All feature requests across every org, filtered by status. Used
    /// by the admin queue page.
    Task<IReadOnlyList<FeatureRequestDto>> ListForAdminAsync(
        FeatureRequestStatus? status, CancellationToken ct = default);

    /// Approve a Pending request. Auto-creates a FeaturedReport row
    /// for HomepageCarousel with a 30-day window so the editorial
    /// approval ships immediately; admins can re-curate via the
    /// existing /api/admin/featured surface afterwards.
    Task<FeatureRequestDto> ApproveAsync(
        Guid actingAdminUserId, Guid requestId, FeatureRequestDecisionRequest req, CancellationToken ct = default);

    /// Reject a Pending request. The org can submit a fresh request
    /// later — Rejected rows don't block the partial-unique index on
    /// Pending.
    Task<FeatureRequestDto> RejectAsync(
        Guid actingAdminUserId, Guid requestId, FeatureRequestDecisionRequest req, CancellationToken ct = default);
}
