using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.API.Authorization;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

/// SuperAdmin-only staff management. Gated on `rbac:*` because the
/// platform RBAC page is seeded SuperAdmin-only — staff management is
/// effectively part of access control, so it shares the same gate
/// instead of inventing a fourth permission group.
[ApiController]
[Route("api/admin/staff")]
[Produces("application/json")]
[Authorize]
[RequirePlatformStaff]
public class AdminStaffController : ControllerBase
{
    private readonly IStaffService _staff;

    public AdminStaffController(IStaffService staff)
    {
        _staff = staff;
    }

    /// <summary>List every platform staff user with their role + 2FA status.</summary>
    [HttpGet]
    [RequirePermission("rbac:view")]
    [ProducesResponseType(typeof(IReadOnlyList<StaffListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await _staff.ListAsync(ct));

    /// <summary>Create a new staff user with the requested platform role.
    /// The new user will be forced through 2FA setup on their first login.</summary>
    [HttpPost]
    [RequirePermission("rbac:create")]
    [ProducesResponseType(typeof(StaffListItemDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateStaffRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        var result = await _staff.CreateAsync(actingUserId, req, ct);
        return CreatedAtAction(nameof(List), new { id = result.Id }, result);
    }

    /// <summary>Replace a staff user's platform role.</summary>
    [HttpPatch("{id:guid}/role")]
    [RequirePermission("rbac:edit")]
    [ProducesResponseType(typeof(StaffListItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateRole(Guid id, [FromBody] UpdateStaffRoleRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        var result = await _staff.UpdateRoleAsync(actingUserId, id, req, ct);
        return Ok(result);
    }

    /// <summary>Soft-delete the staff user. Refuses to remove the last SuperAdmin.</summary>
    [HttpDelete("{id:guid}")]
    [RequirePermission("rbac:delete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        await _staff.DeleteAsync(actingUserId, id, ct);
        return NoContent();
    }

    /// <summary>Wipe a staff user's 2FA configuration (lost-device recovery).
    /// Their next login will route them through the setup wizard again.</summary>
    [HttpPost("{id:guid}/reset-2fa")]
    [RequirePermission("rbac:edit")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetTwoFactor(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        await _staff.ResetTwoFactorAsync(actingUserId, id, ct);
        return NoContent();
    }

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }
}
