using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.API.Authorization;
using Taqreerk.Application.DTOs.Rbac;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

[ApiController]
[Route("api/rbac")]
[Produces("application/json")]
[Authorize]
public class RbacController : ControllerBase
{
    private readonly IRbacService _rbac;

    public RbacController(IRbacService rbac) => _rbac = rbac;

    // ── Pages ────────────────────────────────────────────────────────────────

    [HttpGet("pages")]
    [RequirePermission("rbac:view")]
    public async Task<ActionResult<IReadOnlyList<PageDto>>> GetPages(CancellationToken ct)
        => Ok(await _rbac.GetPagesAsync(ct));

    [HttpPost("pages")]
    [RequirePermission("rbac:create")]
    public async Task<ActionResult<PageDto>> CreatePage([FromBody] CreatePageRequest req, CancellationToken ct)
        => Ok(await _rbac.CreatePageAsync(req, ct));

    [HttpPut("pages/{pageId:guid}")]
    [RequirePermission("rbac:edit")]
    public async Task<ActionResult<PageDto>> UpdatePage(Guid pageId, [FromBody] UpdatePageRequest req, CancellationToken ct)
        => Ok(await _rbac.UpdatePageAsync(pageId, req, ct));

    [HttpDelete("pages/{pageId:guid}")]
    [RequirePermission("rbac:delete")]
    public async Task<IActionResult> DeletePage(Guid pageId, CancellationToken ct)
    {
        await _rbac.DeletePageAsync(pageId, ct);
        return NoContent();
    }

    // ── Permissions ──────────────────────────────────────────────────────────

    [HttpPost("pages/{pageId:guid}/permissions")]
    [RequirePermission("rbac:create")]
    public async Task<ActionResult<PermissionDto>> CreatePermission(Guid pageId, [FromBody] CreatePermissionRequest req, CancellationToken ct)
        => Ok(await _rbac.CreatePermissionAsync(pageId, req, ct));

    [HttpPut("permissions/{permissionId:guid}")]
    [RequirePermission("rbac:edit")]
    public async Task<ActionResult<PermissionDto>> UpdatePermission(Guid permissionId, [FromBody] UpdatePermissionRequest req, CancellationToken ct)
        => Ok(await _rbac.UpdatePermissionAsync(permissionId, req, ct));

    [HttpDelete("permissions/{permissionId:guid}")]
    [RequirePermission("rbac:delete")]
    public async Task<IActionResult> DeletePermission(Guid permissionId, CancellationToken ct)
    {
        await _rbac.DeletePermissionAsync(permissionId, ct);
        return NoContent();
    }

    // ── Roles ────────────────────────────────────────────────────────────────

    [HttpGet("roles")]
    [RequirePermission("rbac:view")]
    public async Task<ActionResult<IReadOnlyList<RoleDto>>> GetRoles(CancellationToken ct)
        => Ok(await _rbac.GetRolesAsync(ct));

    [HttpGet("roles/{roleId:guid}")]
    [RequirePermission("rbac:view")]
    public async Task<ActionResult<RoleDetailDto>> GetRole(Guid roleId, CancellationToken ct)
        => Ok(await _rbac.GetRoleAsync(roleId, ct));

    [HttpPost("roles")]
    [RequirePermission("rbac:create")]
    public async Task<ActionResult<RoleDto>> CreateRole([FromBody] CreateRoleRequest req, CancellationToken ct)
        => Ok(await _rbac.CreateRoleAsync(req, ct));

    [HttpPut("roles/{roleId:guid}")]
    [RequirePermission("rbac:edit")]
    public async Task<ActionResult<RoleDto>> UpdateRole(Guid roleId, [FromBody] UpdateRoleRequest req, CancellationToken ct)
        => Ok(await _rbac.UpdateRoleAsync(roleId, req, ct));

    [HttpDelete("roles/{roleId:guid}")]
    [RequirePermission("rbac:delete")]
    public async Task<IActionResult> DeleteRole(Guid roleId, CancellationToken ct)
    {
        await _rbac.DeleteRoleAsync(roleId, ct);
        return NoContent();
    }

    [HttpPut("roles/{roleId:guid}/permissions")]
    [RequirePermission("rbac:edit")]
    public async Task<ActionResult<RoleDetailDto>> SetRolePermissions(Guid roleId, [FromBody] SetRolePermissionsRequest req, CancellationToken ct)
        => Ok(await _rbac.SetRolePermissionsAsync(roleId, req, ct));

    // ── Users ────────────────────────────────────────────────────────────────

    [HttpGet("users")]
    [RequirePermission("users:view")]
    public async Task<ActionResult<IReadOnlyList<UserRolesDto>>> GetUsers(CancellationToken ct)
        => Ok(await _rbac.GetUsersWithRolesAsync(ct));

    [HttpPut("users/{userId:guid}/roles")]
    [RequirePermission("rbac:edit")]
    public async Task<ActionResult<UserRolesDto>> SetUserRoles(Guid userId, [FromBody] SetUserRolesRequest req, CancellationToken ct)
        => Ok(await _rbac.SetUserRolesAsync(userId, req, ct));
}
