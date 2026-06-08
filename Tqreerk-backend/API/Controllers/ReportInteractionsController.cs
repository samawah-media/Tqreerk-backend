using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.API.Filters;
using Taqreerk.Application.DTOs.Reports;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;

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
    private readonly IUsageService _usage;

    public ReportInteractionsController(
        IReportInteractionsService interactions,
        IUsageService usage)
    {
        _interactions = interactions;
        _usage = usage;
    }

    /// <summary>Mint a signed PDF URL for in-app reading. Consumes one
    /// monthly read on the caller's plan (idempotent per report/month).</summary>
    [HttpGet("{id:guid}/full-access")]
    [Authorize]
    [ProducesResponseType(typeof(ReportFullAccessDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> FullAccess(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var dto = await _usage.EnsureWithinLimitAndConsumeAsync(
            userId,
            UsageActionType.ReportFullAccess,
            id,
            token => _interactions.GetFullAccessAsync(userId, id, token),
            ct,
            idempotentPerResource: true);
        return Ok(dto);
    }

    /// <summary>Rate (or update my rating on) a report. 1..5 stars, optional review note.</summary>
    [HttpPut("{id:guid}/rating")]
    [Authorize]
    [RequiresPlanFeature(nameof(Plan.HasInteractions))]
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
    [RequiresPlanFeature(nameof(Plan.HasInteractions))]
    [ProducesResponseType(typeof(ReportInteractionStateDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Unrate(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _interactions.UnrateAsync(userId, id, ct));
    }

    /// <summary>Save this report to my "saved reports" list.</summary>
    [HttpPost("{id:guid}/save")]
    [Authorize]
    [RequiresPlanFeature(nameof(Plan.HasInteractions))]
    [ProducesResponseType(typeof(ReportInteractionStateDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Save(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();

        if (await _interactions.IsSavedAsync(userId, id, ct))
            return Ok(await _interactions.SaveAsync(userId, id, ct));

        var result = await _usage.EnsureWithinLimitAndConsumeAsync(
            userId,
            UsageActionType.SaveReport,
            id,
            token => _interactions.SaveAsync(userId, id, token),
            ct);
        return Ok(result);
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
    [RequiresPlanFeature(nameof(Plan.HasInteractions))]
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
    /// window keeps refresh-spam from inflating the counter. Does not
    /// consume plan read quota — reads are gated on /full-access.</summary>
    [HttpPost("{id:guid}/view")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> RecordView(Guid id, CancellationToken ct)
    {
        Guid? userId = TryGetUserId(out var uid) ? uid : null;

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var ua = HttpContext.Request.Headers.UserAgent.ToString();
        await _interactions.RecordViewAsync(id, userId, ip, ua, ct);

        return NoContent();
    }

    /// <summary>Unlock the smart summary for this report. Consumes one
    /// <c>AiSummarize</c> slot on the caller's plan (idempotent per
    /// report/month — re-viewing the same report is free).</summary>
    [HttpPost("{id:guid}/ai-summary-view")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RecordAiSummaryView(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();

        await _usage.EnsureWithinLimitAndConsumeAsync(
            userId,
            UsageActionType.AiSummarize,
            id,
            static token => Task.CompletedTask,
            ct,
            idempotentPerResource: true);

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
