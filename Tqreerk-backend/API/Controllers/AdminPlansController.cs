using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.API.Authorization;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

/// Admin curation surface for the plan catalogue. Read-only list for
/// the table + a single PATCH for edits — Create / Delete are
/// intentionally not exposed in v1 because the four canonical plans
/// are load-bearing across the rest of the app (registration auto-
/// link, downloads percentage rule, compare cap). Adding a fifth tier
/// stays a SQL migration so the dev sees the wider impact.
///
/// Permissions reuse the existing `subscriptions:view` /
/// `subscriptions:edit` slots — the admin app groups plans + active
/// subscriptions under one mental model, so a separate "plans" page
/// permission would be friction without payoff.
[ApiController]
[Route("api/admin/plans")]
[Produces("application/json")]
[Authorize]
[RequirePlatformStaff]
public class AdminPlansController : ControllerBase
{
    private readonly IAdminPlansService _plans;

    public AdminPlansController(IAdminPlansService plans)
    {
        _plans = plans;
    }

    /// <summary>Every plan in the catalogue, ordered by target type and
    /// price. Each row carries the live count of active subscriptions
    /// so the admin can spot before they push a destructive edit.</summary>
    [HttpGet]
    [RequirePermission("subscriptions:view")]
    [ProducesResponseType(typeof(IReadOnlyList<AdminPlanDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await _plans.ListAsync(ct));

    /// <summary>Single plan with full editable surface.</summary>
    [HttpGet("{id:guid}")]
    [RequirePermission("subscriptions:view")]
    [ProducesResponseType(typeof(AdminPlanDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        => Ok(await _plans.GetAsync(id, ct));

    /// <summary>Patch any subset of the plan's editable columns. Audit
    /// log captures the full before/after diff.</summary>
    [HttpPatch("{id:guid}")]
    [RequirePermission("subscriptions:edit")]
    [ProducesResponseType(typeof(AdminPlanDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid id, [FromBody] UpdateAdminPlanRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        return Ok(await _plans.UpdateAsync(actingUserId, id, req, ip, ct));
    }

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }
}
