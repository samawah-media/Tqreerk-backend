using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.API.Authorization;
using Taqreerk.Application.DTOs.FeatureRequests;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Enums;

namespace Taqreerk.API.Controllers;

/// Admin queue for org-submitted feature requests. Approving an entry
/// auto-creates a HomepageCarousel FeaturedReport row with a 30-day
/// window so the editorial decision ships immediately; admins can
/// re-curate via /api/admin/featured afterwards. Permissions reuse the
/// existing `featured:*` slots — view to read the queue, edit to act
/// on rows.
[ApiController]
[Route("api/admin/feature-requests")]
[Produces("application/json")]
[Authorize]
[RequirePlatformStaff]
public class AdminFeatureRequestsController : ControllerBase
{
    private readonly IFeatureRequestsService _service;

    public AdminFeatureRequestsController(IFeatureRequestsService service)
    {
        _service = service;
    }

    /// <summary>The full queue, optionally filtered by status. Newest
    /// first. Pending is the typical filter for the inbox view.</summary>
    [HttpGet]
    [RequirePermission("featured:view")]
    [ProducesResponseType(typeof(IReadOnlyList<FeatureRequestDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] FeatureRequestStatus? status,
        CancellationToken ct)
        => Ok(await _service.ListForAdminAsync(status, ct));

    /// <summary>Approve a Pending request. Creates a HomepageCarousel
    /// FeaturedReport entry server-side; the admin can adjust the
    /// section/window from /api/admin/featured later.</summary>
    [HttpPost("{id:guid}/approve")]
    [RequirePermission("featured:edit")]
    [ProducesResponseType(typeof(FeatureRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Approve(
        Guid id, [FromBody] FeatureRequestDecisionRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        return Ok(await _service.ApproveAsync(actingUserId, id, req, ct));
    }

    /// <summary>Reject a Pending request. Optionally captures a note
    /// the org can read on their feature-requests list.</summary>
    [HttpPost("{id:guid}/reject")]
    [RequirePermission("featured:edit")]
    [ProducesResponseType(typeof(FeatureRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Reject(
        Guid id, [FromBody] FeatureRequestDecisionRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        return Ok(await _service.RejectAsync(actingUserId, id, req, ct));
    }

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }
}
