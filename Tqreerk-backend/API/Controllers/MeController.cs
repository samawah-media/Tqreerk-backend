using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.Application.DTOs.Me;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

/// Caller-scoped reads for the individual dashboard. Points and usage
/// each have their own controller; this one covers the bits that are too
/// small to justify their own service slot (saved-reports listing,
/// activity feed).
[ApiController]
[Route("api/me")]
[Produces("application/json")]
[Authorize]
public class MeController : ControllerBase
{
    private readonly IMeService _me;

    public MeController(IMeService me)
    {
        _me = me;
    }

    /// <summary>The caller's saved reports (newest first).</summary>
    [HttpGet("saved-reports")]
    [ProducesResponseType(typeof(IReadOnlyList<MySavedReportDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SavedReports(
        [FromQuery] int take = 20,
        CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _me.ListSavedReportsAsync(userId, take, ct));
    }

    /// <summary>Recent metered actions performed by the caller, joined
    /// with the report they targeted (when applicable).</summary>
    [HttpGet("activity")]
    [ProducesResponseType(typeof(IReadOnlyList<MyActivityItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Activity(
        [FromQuery] int take = 10,
        CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _me.ListActivityAsync(userId, take, ct));
    }

    /// <summary>Personalised recommendations: recent published reports from
    /// the caller's sector interests, excluding ones already saved. Sorted
    /// by last activity so the feed stays current. Empty list when the
    /// user hasn't picked any interests yet.</summary>
    [HttpGet("recommendations")]
    [ProducesResponseType(typeof(IReadOnlyList<MySavedReportDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Recommendations(
        [FromQuery] int take = 20,
        CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _me.ListRecommendationsAsync(userId, take, ct));
    }

    /// <summary>Compact projection of the caller's active plan + this-
    /// month usage snapshot. The SPA caches this to drive pre-emptive
    /// gating (hide / disable controls) without round-tripping for
    /// every action. Refresh on plan change or month rollover.</summary>
    [HttpGet("plan-features")]
    [ProducesResponseType(typeof(PlanFeaturesDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> PlanFeatures(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _me.GetPlanFeaturesAsync(userId, ct));
    }

    /// <summary>Caller subscription row for settings / checkout banners.
    /// 204 when no row exists.</summary>
    [HttpGet("subscription")]
    [ProducesResponseType(typeof(MySubscriptionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Subscription(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var dto = await _me.GetSubscriptionAsync(userId, ct);
        if (dto is null) return NoContent();
        return Ok(dto);
    }

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }
}
