using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.Application.DTOs.Points;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

/// Read-only surface over the per-user points balance & transaction log.
/// Mutations (credit / debit) are performed by other services via
/// IPointsService — they are intentionally NOT exposed here.
[ApiController]
[Route("api/me/points")]
[Produces("application/json")]
[Authorize]
public class PointsController : ControllerBase
{
    private readonly IPointsService _points;

    public PointsController(IPointsService points)
    {
        _points = points;
    }

    /// <summary>Caller's current points balance + welcome amount + last update.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PointsBalanceDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMine(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _points.GetMyBalanceAsync(userId, ct));
    }

    /// <summary>Paged credit/debit history (newest first).</summary>
    [HttpGet("history")]
    [ProducesResponseType(typeof(PointsHistoryPageDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHistory(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _points.GetMyHistoryAsync(userId, page, pageSize, ct));
    }

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }
}
