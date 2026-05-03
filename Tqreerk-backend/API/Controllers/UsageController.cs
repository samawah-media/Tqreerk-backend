using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.Application.DTOs.Usage;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

/// Read-only surface over the per-user freemium counters. Powers the
/// dashboard "X of Y used this month" widget and the usage history page
/// in the individual's account section.
[ApiController]
[Route("api/usage")]
[Produces("application/json")]
[Authorize]
public class UsageController : ControllerBase
{
    private readonly IUsageService _usage;

    public UsageController(IUsageService usage)
    {
        _usage = usage;
    }

    /// <summary>Current month's usage counters for the caller, with the
    /// active plan's limits and the next reset timestamp.</summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(UsageSummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMe(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _usage.GetMyUsageAsync(userId, ct));
    }

    /// <summary>Paged history of metered actions (newest first). Useful
    /// for the "Activity" tab on the user's settings page.</summary>
    [HttpGet("me/history")]
    [ProducesResponseType(typeof(UsageHistoryPageDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHistory(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _usage.GetMyHistoryAsync(userId, page, pageSize, ct));
    }

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }
}
