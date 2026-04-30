using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.API.Authorization;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.DTOs.Reports;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

/// Admin-side organization management. Distinct from /api/organizations
/// (org-side, scoped to the caller's own org). Read endpoints are gated on
/// `organizations:view`, writes split between `:edit` and `:delete`.
[ApiController]
[Route("api/admin/organizations")]
[Produces("application/json")]
[Authorize]
[RequirePlatformStaff]
public class AdminOrganizationsController : ControllerBase
{
    private readonly IAdminOrganizationsService _service;

    public AdminOrganizationsController(IAdminOrganizationsService service)
    {
        _service = service;
    }

    /// <summary>List organizations with filters + pagination.</summary>
    [HttpGet]
    [RequirePermission("organizations:view")]
    [ProducesResponseType(typeof(PagedResult<AdminOrganizationListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] AdminOrganizationsListRequest req, CancellationToken ct)
        => Ok(await _service.ListAsync(req, ct));

    /// <summary>Detail for a single organization (counts + editable fields).</summary>
    [HttpGet("{id:guid}")]
    [RequirePermission("organizations:view")]
    [ProducesResponseType(typeof(AdminOrganizationDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        => Ok(await _service.GetAsync(id, ct));

    /// <summary>Edit basic fields. Only set properties are applied.</summary>
    [HttpPatch("{id:guid}")]
    [RequirePermission("organizations:edit")]
    [ProducesResponseType(typeof(AdminOrganizationDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAdminOrganizationRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        return Ok(await _service.UpdateAsync(actingUserId, id, req, ct));
    }

    /// <summary>Mark verified (blue badge). Idempotent.</summary>
    [HttpPost("{id:guid}/verify")]
    [RequirePermission("organizations:edit")]
    [ProducesResponseType(typeof(AdminOrganizationDetailDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Verify(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        return Ok(await _service.SetVerifiedAsync(actingUserId, id, true, ct));
    }

    /// <summary>Clear the verified badge.</summary>
    [HttpPost("{id:guid}/unverify")]
    [RequirePermission("organizations:edit")]
    [ProducesResponseType(typeof(AdminOrganizationDetailDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Unverify(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        return Ok(await _service.SetVerifiedAsync(actingUserId, id, false, ct));
    }

    /// <summary>Suspend the organization. Members can no longer log in until
    /// reactivated. Reason is required and stored on the audit row.</summary>
    [HttpPost("{id:guid}/suspend")]
    [RequirePermission("organizations:edit")]
    [ProducesResponseType(typeof(AdminOrganizationDetailDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Suspend(Guid id, [FromBody] SuspendOrganizationRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        return Ok(await _service.SuspendAsync(actingUserId, id, req, ct));
    }

    /// <summary>Reactivate a suspended organization.</summary>
    [HttpPost("{id:guid}/reactivate")]
    [RequirePermission("organizations:edit")]
    [ProducesResponseType(typeof(AdminOrganizationDetailDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Reactivate(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        return Ok(await _service.ReactivateAsync(actingUserId, id, ct));
    }

    /// <summary>Soft-delete the organization. Refuses if there are published
    /// reports — those need to be archived first.</summary>
    [HttpDelete("{id:guid}")]
    [RequirePermission("organizations:delete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        await _service.DeleteAsync(actingUserId, id, ct);
        return NoContent();
    }

    /// <summary>Reports owned by this organization, paginated.</summary>
    [HttpGet("{id:guid}/reports")]
    [RequirePermission("organizations:view")]
    [ProducesResponseType(typeof(PagedResult<AdminOrganizationReportItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListReports(
        Guid id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
        => Ok(await _service.ListReportsAsync(id, page, pageSize, ct));

    /// <summary>Members of this organization with role + activity flag.</summary>
    [HttpGet("{id:guid}/members")]
    [RequirePermission("organizations:view")]
    [ProducesResponseType(typeof(IReadOnlyList<AdminOrganizationMemberDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListMembers(Guid id, CancellationToken ct)
        => Ok(await _service.ListMembersAsync(id, ct));

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }
}
