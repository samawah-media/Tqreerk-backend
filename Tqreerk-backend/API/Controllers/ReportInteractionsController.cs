using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.Application.DTOs.Reports;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

/// Per-user interactions on a published report — rate, save, recommend,
/// record-a-view. Sits next to ReportsController on /api/reports/{id}/...
/// because the surface is logically the same resource even though the
/// org-side write endpoints (upload / resubmit / archive) live there too.
///
/// All endpoints require auth EXCEPT the view-recorder, which we expose
/// anonymously so the public report page can fire-and-forget on mount.
[ApiController]
[Route("api/reports")]
[Produces("application/json")]
public class ReportInteractionsController : ControllerBase
{
    private readonly IReportInteractionsService _interactions;

    public ReportInteractionsController(IReportInteractionsService interactions)
    {
        _interactions = interactions;
    }

    /// <summary>Rate (or update my rating on) a report. 1..5 stars, optional review note.</summary>
    [HttpPut("{id:guid}/rating")]
    [Authorize]
    [ProducesResponseType(typeof(ReportInteractionStateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Rate(Guid id, [FromBody] RateReportRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _interactions.RateAsync(userId, id, req, ct));
    }

    /// <summary>Clear my rating on this report.</summary>
    [HttpDelete("{id:guid}/rating")]
    [Authorize]
    [ProducesResponseType(typeof(ReportInteractionStateDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Unrate(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _interactions.UnrateAsync(userId, id, ct));
    }

    /// <summary>Save this report to my "saved reports" list.</summary>
    [HttpPost("{id:guid}/save")]
    [Authorize]
    [ProducesResponseType(typeof(ReportInteractionStateDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Save(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _interactions.SaveAsync(userId, id, ct));
    }

    /// <summary>Remove this report from my saved list.</summary>
    [HttpDelete("{id:guid}/save")]
    [Authorize]
    [ProducesResponseType(typeof(ReportInteractionStateDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Unsave(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _interactions.UnsaveAsync(userId, id, ct));
    }

    /// <summary>Recommend this report. Optional ?channel=... annotates
    /// the row for downstream analytics (twitter / whatsapp / link).</summary>
    [HttpPost("{id:guid}/recommend")]
    [Authorize]
    [ProducesResponseType(typeof(ReportInteractionStateDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Recommend(
        Guid id, [FromQuery(Name = "channel")] string? channel, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _interactions.RecommendAsync(userId, id, channel, ct));
    }

    /// <summary>Withdraw a recommendation.</summary>
    [HttpDelete("{id:guid}/recommend")]
    [Authorize]
    [ProducesResponseType(typeof(ReportInteractionStateDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Unrecommend(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _interactions.UnrecommendAsync(userId, id, ct));
    }

    /// <summary>Record an anonymous view. Per-IP+report dedupe in a 1-hour
    /// window keeps refresh-spam from inflating the counter.</summary>
    [HttpPost("{id:guid}/view")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RecordView(Guid id, CancellationToken ct)
    {
        // Try to attach the user id when there's a token, so authenticated
        // views still get a UserId on the row even though the endpoint is
        // anonymous-callable. TryGetUserId returns false on guests.
        Guid? userId = TryGetUserId(out var uid) ? uid : null;

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var ua = HttpContext.Request.Headers.UserAgent.ToString();
        await _interactions.RecordViewAsync(id, userId, ip, ua, ct);
        return NoContent();
    }

    /// <summary>User-specific interaction snapshot. The public report
    /// page calls this after the anonymous detail load when there's a
    /// token, so it can render the right button states.</summary>
    [HttpGet("{id:guid}/me")]
    [Authorize]
    [ProducesResponseType(typeof(MyReportInteractionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMyState(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _interactions.GetMyStateAsync(userId, id, ct));
    }

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }
}
