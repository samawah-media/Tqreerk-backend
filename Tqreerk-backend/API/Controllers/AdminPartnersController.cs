using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.API.Authorization;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

[ApiController]
[Route("api/admin/partners")]
[Produces("application/json")]
[Authorize]
[RequirePlatformStaff]
public class AdminPartnersController : ControllerBase
{
    private readonly IAdminPartnersService _service;

    public AdminPartnersController(IAdminPartnersService service)
    {
        _service = service;
    }

    [HttpGet]
    [RequirePermission("partners:view")]
    [ProducesResponseType(typeof(IReadOnlyList<AdminPartnerDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await _service.ListAsync(ct));

    [HttpPost]
    [RequirePermission("partners:create")]
    [ProducesResponseType(typeof(AdminPartnerDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(
        [FromForm] CreatePartnerRequest req,
        IFormFile? logo,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        var created = await _service.CreateAsync(actingUserId, req, logo, ct);
        return CreatedAtAction(nameof(List), new { id = created.Id }, created);
    }

    [HttpPatch("{id:guid}")]
    [RequirePermission("partners:edit")]
    [ProducesResponseType(typeof(AdminPartnerDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromForm] UpdatePartnerRequest req,
        IFormFile? logo,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        return Ok(await _service.UpdateAsync(actingUserId, id, req, logo, ct));
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission("partners:delete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        await _service.DeleteAsync(actingUserId, id, ct);
        return NoContent();
    }

    [HttpPost("reorder")]
    [RequirePermission("partners:edit")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Reorder([FromBody] ReorderRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        await _service.ReorderAsync(actingUserId, req, ct);
        return NoContent();
    }

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }
}
