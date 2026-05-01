using System.ComponentModel.DataAnnotations;

namespace Taqreerk.Application.DTOs.Reports;

/// One row in the comments list under a public report.
public record ReportCommentDto(
    Guid Id,
    Guid ReportId,
    Guid UserId,
    string UserFullName,
    string Body,
    DateTime CreatedAt,
    /// True when the calling user owns this comment — drives the "delete"
    /// affordance in the SPA. Anonymous callers always get false.
    bool IsMine
);

/// Body of POST /api/reports/{id}/comments.
public record CreateCommentRequest(
    [Required, StringLength(4000, MinimumLength = 1)] string Body
);
