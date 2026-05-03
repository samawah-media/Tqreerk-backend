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

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }
}
