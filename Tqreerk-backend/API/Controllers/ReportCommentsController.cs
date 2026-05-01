using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.Application.DTOs.Reports;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

/// Public-readable comments on a published report. Sits next to the
/// interactions controller on /api/reports/{id}/comments because it's
/// part of the same resource surface — keeping them on separate
/// controllers just to decouple auth attributes per endpoint.
[ApiController]
[Route("api/reports")]
[Produces("application/json")]
public class ReportCommentsController : ControllerBase
{
    private readonly IReportCommentsService _comments;

    public ReportCommentsController(IReportCommentsService comments)
    {
        _comments = comments;
    }

    /// <summary>Newest-first list of comments. Anonymous-readable.</summary>
    [HttpGet("{id:guid}/comments")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(PagedResult<ReportCommentDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        Guid id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        // Anonymous callers still get a list; we just don't flag any
        // rows as `IsMine`. TryGetUserId returns false on a missing or
        // unparseable `sub` claim.
        Guid? viewerId = TryGetUserId(out var uid) ? uid : null;
        return Ok(await _comments.ListAsync(id, viewerId, page, pageSize, ct));
    }

    /// <summary>Append a comment. Auth required.</summary>
    [HttpPost("{id:guid}/comments")]
    [Authorize]
    [ProducesResponseType(typeof(ReportCommentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create(Guid id, [FromBody] CreateCommentRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var created = await _comments.CreateAsync(userId, id, req, ct);
        return CreatedAtAction(nameof(List), new { id }, created);
    }

    /// <summary>Soft-delete a comment. Owner only.</summary>
    [HttpDelete("{id:guid}/comments/{commentId:guid}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, Guid commentId, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        // `id` (the report) is part of the route for cache-key cleanliness;
        // service-side authz only cares about the comment id and caller.
        _ = id;
        await _comments.DeleteAsync(userId, commentId, ct);
        return NoContent();
    }

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }
}
