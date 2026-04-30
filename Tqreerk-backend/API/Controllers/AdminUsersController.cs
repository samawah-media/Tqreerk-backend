using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.API.Authorization;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.DTOs.Reports;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

/// Admin-side user management. Read endpoints are gated on `users:view`,
/// writes split between `:edit` (ban/unban) and `:delete`. All writes go
/// through the audit logger.
///
/// Note: platform staff users have their own management surface at
/// /api/admin/staff. The endpoints here refuse to act on staff accounts
/// (the service throws on those). The list endpoint, however, can include
/// staff when filtered to userType=staff so the admin can see them.
[ApiController]
[Route("api/admin/users")]
[Produces("application/json")]
[Authorize]
[RequirePlatformStaff]
public class AdminUsersController : ControllerBase
{
    private readonly IAdminUsersService _service;

    public AdminUsersController(IAdminUsersService service)
    {
        _service = service;
    }

    /// <summary>List users with filters + pagination.</summary>
    [HttpGet]
    [RequirePermission("users:view")]
    [ProducesResponseType(typeof(PagedResult<AdminUserListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] AdminUsersListRequest req, CancellationToken ct)
        => Ok(await _service.ListAsync(req, ct));

    /// <summary>Detail for a single user (org memberships + uploaded count).</summary>
    [HttpGet("{id:guid}")]
    [RequirePermission("users:view")]
    [ProducesResponseType(typeof(AdminUserDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        => Ok(await _service.GetAsync(id, ct));

    /// <summary>Suspend the user. Reason required (≥ 5 chars). Refuses
    /// when the target is yourself or a platform staff member.</summary>
    [HttpPost("{id:guid}/ban")]
    [RequirePermission("users:edit")]
    [ProducesResponseType(typeof(AdminUserDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Ban(Guid id, [FromBody] BanUserRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        return Ok(await _service.BanAsync(actingUserId, id, req, ct));
    }

    /// <summary>Lift the suspension on a user.</summary>
    [HttpPost("{id:guid}/unban")]
    [RequirePermission("users:edit")]
    [ProducesResponseType(typeof(AdminUserDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Unban(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        return Ok(await _service.UnbanAsync(actingUserId, id, ct));
    }

    /// <summary>Soft-delete the user. Refuses self-delete, staff accounts,
    /// and users who own an organization (transfer the org first).</summary>
    [HttpDelete("{id:guid}")]
    [RequirePermission("users:delete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        await _service.DeleteAsync(actingUserId, id, ct);
        return NoContent();
    }

    /// <summary>Reports the user has uploaded across all their orgs.</summary>
    [HttpGet("{id:guid}/reports")]
    [RequirePermission("users:view")]
    [ProducesResponseType(typeof(PagedResult<AdminUserReportItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListReports(
        Guid id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
        => Ok(await _service.ListReportsAsync(id, page, pageSize, ct));

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }
}
