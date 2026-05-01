using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.API.Authorization;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

/// SuperAdmin surface for system_settings, maintenance toggle, and the
/// per-service health dashboard. All endpoints are gated by the existing
/// `settings:*` RBAC permissions (SuperAdmin only in the seed).
[ApiController]
[Route("api/admin")]
[Produces("application/json")]
[Authorize]
[RequirePlatformStaff]
public class AdminSettingsController : ControllerBase
{
    private readonly IAdminSettingsService _service;

    public AdminSettingsController(IAdminSettingsService service)
    {
        _service = service;
    }

    /// <summary>All settings, ordered by category then key.</summary>
    [HttpGet("settings")]
    [RequirePermission("settings:view")]
    [ProducesResponseType(typeof(IReadOnlyList<SystemSettingDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListSettings(CancellationToken ct)
        => Ok(await _service.ListAsync(ct));

    /// <summary>Update a setting's value. Refuses unknown keys — adding
    /// a new setting requires a code change to the seed.</summary>
    [HttpPatch("settings/{key}")]
    [RequirePermission("settings:edit")]
    [ProducesResponseType(typeof(SystemSettingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateSetting(string key, [FromBody] UpdateSettingRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        return Ok(await _service.UpdateAsync(actingUserId, key, req, ct));
    }

    /// <summary>Turn maintenance mode on. Public traffic gets a 503 with
    /// `{ maintenance: true, message: ... }` until disabled.</summary>
    [HttpPost("maintenance/enable")]
    [RequirePermission("settings:edit")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> EnableMaintenance(CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        await _service.SetMaintenanceModeAsync(actingUserId, true, ct);
        return NoContent();
    }

    /// <summary>Lift maintenance mode.</summary>
    [HttpPost("maintenance/disable")]
    [RequirePermission("settings:edit")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DisableMaintenance(CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        await _service.SetMaintenanceModeAsync(actingUserId, false, ct);
        return NoContent();
    }

    /// <summary>Aggregate health snapshot — DB + AI service today, more
    /// services as their probes get wired.</summary>
    [HttpGet("health")]
    [RequirePermission("settings:view")]
    [ProducesResponseType(typeof(AdminHealthDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Health(CancellationToken ct)
        => Ok(await _service.GetHealthAsync(ct));

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }
}
