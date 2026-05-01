using Taqreerk.Application.DTOs.Reports;

namespace Taqreerk.Application.Interfaces;

/// Public-readable comments on a published report. Single-level — no
/// replies — until threading becomes a real ask. Posting requires auth;
/// deletion is restricted to the comment's owner (admin moderation will
/// land alongside the audit-log viewer feature).
public interface IReportCommentsService
{
    /// Newest-first list of comments, paginated. `viewerUserId` is null
    /// for anonymous callers — used only to flag the `IsMine` field on
    /// each row.
    Task<PagedResult<ReportCommentDto>> ListAsync(
        Guid reportId, Guid? viewerUserId, int page, int pageSize, CancellationToken ct = default);

    /// Append a new comment. Throws KeyNotFoundException when the report
    /// is missing or unpublished.
    Task<ReportCommentDto> CreateAsync(
        Guid userId, Guid reportId, CreateCommentRequest req, CancellationToken ct = default);

    /// Soft-delete a comment. Refuses unless the caller is the comment's
    /// owner.
    Task DeleteAsync(Guid userId, Guid commentId, CancellationToken ct = default);

    /// Cheap COUNT used by the public detail endpoint.
    Task<int> CountForReportAsync(Guid reportId, CancellationToken ct = default);
}
