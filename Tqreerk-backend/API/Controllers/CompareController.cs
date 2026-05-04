using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.API.Filters;
using Taqreerk.Application.DTOs.Compare;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Enums;

namespace Taqreerk.API.Controllers;

/// AI-powered report comparison surface for individual users. The
/// freemium gate fires on POST /api/ai/compare via the
/// [EnforceUsageLimit] filter; cache hits inside CompareService
/// short-circuit *before* the gate would burn a slot, so re-viewing a
/// prior comparison stays free.
///
/// History endpoints live at /api/me/comparisons because the rows are
/// per-user (not per-org); same surface as /api/me/saved-reports etc.
[ApiController]
[Authorize]
[Produces("application/json")]
public class CompareController : ControllerBase
{
    private readonly ICompareService _compare;

    public CompareController(ICompareService compare)
    {
        _compare = compare;
    }

    /// <summary>Run a comparison on 2..4 published reports. Returns the
    /// rich result (per-report metadata, pairwise similarity, Gemini
    /// qualitative output). Cache-aware — repeating the same set
    /// reuses the prior row without burning the user's monthly cap.</summary>
    [HttpPost("api/ai/compare")]
    [EnforceUsageLimit(UsageActionType.AiCompare)]
    [ProducesResponseType(typeof(ComparisonResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Compare(
        [FromBody] CreateComparisonRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await _compare.CompareAsync(userId, req.ReportIds, ct);
        return Ok(result);
    }

    /// <summary>The caller's comparison history (newest first). Useful
    /// for a "my comparisons" list later — surfaced now so the
    /// frontend has somewhere to deep-link prior runs.</summary>
    [HttpGet("api/me/comparisons")]
    [ProducesResponseType(typeof(IReadOnlyList<ComparisonListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListMine(
        [FromQuery] int take = 20, CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _compare.ListMineAsync(userId, take, ct));
    }

    /// <summary>Re-render a specific comparison without re-running the
    /// AI. Caller must own the row.</summary>
    [HttpGet("api/me/comparisons/{id:guid}")]
    [ProducesResponseType(typeof(ComparisonResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMine(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _compare.GetMineAsync(userId, id, ct));
    }

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }
}
