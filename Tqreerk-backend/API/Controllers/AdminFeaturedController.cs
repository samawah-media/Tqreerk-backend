using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.API.Authorization;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

/// Admin curation of the public Featured slots. Org-side request flow
/// (Feature 7 of the orgs plan) is intentionally NOT here yet — it'll
/// land alongside its upstream when that ships.
[ApiController]
[Route("api/admin/featured")]
[Produces("application/json")]
[Authorize]
[RequirePlatformStaff]
public class AdminFeaturedController : ControllerBase
{
    private readonly IAdminFeaturedService _service;

    public AdminFeaturedController(IAdminFeaturedService service)
    {
        _service = service;
    }

    /// <summary>All featured rows across every section, ordered by
    /// section then position. The SPA groups them client-side into the
    /// kanban columns.</summary>
    [HttpGet]
    [RequirePermission("featured:view")]
    [ProducesResponseType(typeof(IReadOnlyList<FeaturedReportDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await _service.ListAsync(ct));

    /// <summary>Pin a published report to a section. Adds to the end of
    /// that section; reorder afterwards via /sections/{section}/reorder.</summary>
    [HttpPost]
    [RequirePermission("featured:create")]
    [ProducesResponseType(typeof(FeaturedReportDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create([FromBody] CreateFeaturedReportRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        var created = await _service.CreateAsync(actingUserId, req, ct);
        return CreatedAtAction(nameof(List), new { id = created.Id }, created);
    }

    /// <summary>Edit window / activation / section.</summary>
    [HttpPatch("{id:guid}")]
    [RequirePermission("featured:edit")]
    [ProducesResponseType(typeof(FeaturedReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateFeaturedReportRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        return Ok(await _service.UpdateAsync(actingUserId, id, req, ct));
    }

    /// <summary>Remove the featured row.</summary>
    [HttpDelete("{id:guid}")]
    [RequirePermission("featured:delete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        await _service.DeleteAsync(actingUserId, id, ct);
        return NoContent();
    }

    /// <summary>Reorder a section's rows. The Ids array is the new order.</summary>
    [HttpPost("sections/{section}/reorder")]
    [RequirePermission("featured:edit")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Reorder(string section, [FromBody] FeaturedReorderRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        await _service.ReorderSectionAsync(actingUserId, section, req, ct);
        return NoContent();
    }

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }
}
