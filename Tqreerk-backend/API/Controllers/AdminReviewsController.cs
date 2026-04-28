using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.API.Authorization;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.DTOs.Reports;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

/// Endpoints powering the moderation queue. Reviewers see what's waiting,
/// claim a report to work on, release it back if they can't finish, and
/// fetch the full report payload for the workspace page. The actual
/// approve/reject/return decisions land in PR A4 — this controller stops at
/// claim + release + read.
[ApiController]
[Route("api/admin/reviews")]
[Produces("application/json")]
[Authorize]
[RequirePlatformStaff]
public class AdminReviewsController : ControllerBase
{
    private readonly IReviewService _reviews;

    public AdminReviewsController(IReviewService reviews)
    {
        _reviews = reviews;
    }

    /// <summary>Paginated review queue. By default returns reports in
    /// PendingReview plus the calling reviewer's own UnderReview claims.</summary>
    [HttpGet("queue")]
    [ProducesResponseType(typeof(PagedResult<ReviewQueueItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetQueue(
        [FromQuery(Name = "sectorId")] Guid? sectorId = null,
        [FromQuery(Name = "organizationId")] Guid? organizationId = null,
        [FromQuery(Name = "assignedToMe")] bool? assignedToMe = null,
        [FromQuery(Name = "sort")] string? sort = null,
        [FromQuery(Name = "page")] int page = 1,
        [FromQuery(Name = "pageSize")] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var req = new ReviewQueueRequest(
            SectorId: sectorId,
            OrganizationId: organizationId,
            AssignedToMe: assignedToMe,
            Sort: sort,
            Page: page,
            PageSize: pageSize);
        return Ok(await _reviews.GetQueueAsync(userId, req, ct));
    }

    /// <summary>Atomically claim a report. 409 if another reviewer has it.</summary>
    [HttpPost("{id:guid}/claim")]
    [ProducesResponseType(typeof(ReportForReviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Claim(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _reviews.ClaimAsync(userId, id, ct));
    }

    /// <summary>Release the claim back to the queue. Only the claim-holder
    /// can release.</summary>
    [HttpPost("{id:guid}/release")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Release(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        await _reviews.ReleaseAsync(userId, id, ct);
        return NoContent();
    }

    /// <summary>Full report detail for the workspace page (metadata + signed
    /// PDF URL + prior review history).</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ReportForReviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _reviews.GetForReviewAsync(userId, id, ct));
    }

    /// <summary>Approve the claimed report. Triggers the AI pipeline.</summary>
    [HttpPost("{id:guid}/approve")]
    [ProducesResponseType(typeof(ReportForReviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Approve(
        Guid id,
        [FromBody] ApproveDecisionRequest? req,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _reviews.ApproveAsync(userId, id, req ?? new ApproveDecisionRequest(), ct));
    }

    /// <summary>Reject the claimed report. Notes are required (≥10 chars).
    /// Terminal — the org cannot resubmit.</summary>
    [HttpPost("{id:guid}/reject")]
    [ProducesResponseType(typeof(ReportForReviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reject(
        Guid id,
        [FromBody] RejectDecisionRequest req,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _reviews.RejectAsync(userId, id, req, ct));
    }

    /// <summary>Send the claimed report back to the org for edits. Notes
    /// are required (≥10 chars). The org can re-upload from their
    /// dashboard.</summary>
    [HttpPost("{id:guid}/return-for-edit")]
    [ProducesResponseType(typeof(ReportForReviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReturnForEdit(
        Guid id,
        [FromBody] ReturnForEditDecisionRequest req,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _reviews.ReturnForEditAsync(userId, id, req, ct));
    }

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }
}
