using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.API.Authorization;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

/// Admin CRUD for sectors and countries. Both surfaces live on one
/// controller because they share the same shape (small lookup tables,
/// drag-and-drop ordering, reference-count guarded deletes) and the same
/// SuperAdmin-only RBAC gate.
///
/// All endpoints sit behind the new "categories" page in RBAC, granted
/// only to SuperAdmin in the seed. Public consumers (signup wizard, org
/// editor) keep using the existing /api/countries and /api/sectors
/// endpoints — those remain anonymous.
[ApiController]
[Route("api/admin")]
[Produces("application/json")]
[Authorize]
[RequirePlatformStaff]
public class AdminCategoriesController : ControllerBase
{
    private readonly IAdminCategoriesService _service;

    public AdminCategoriesController(IAdminCategoriesService service)
    {
        _service = service;
    }

    // ── Sectors ──────────────────────────────────────────────────────────────

    [HttpGet("sectors")]
    [RequirePermission("categories:view")]
    [ProducesResponseType(typeof(IReadOnlyList<AdminSectorDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListSectors(CancellationToken ct)
        => Ok(await _service.ListSectorsAsync(ct));

    [HttpPost("sectors")]
    [RequirePermission("categories:create")]
    [ProducesResponseType(typeof(AdminSectorDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateSector([FromBody] CreateSectorRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        var created = await _service.CreateSectorAsync(actingUserId, req, ct);
        return CreatedAtAction(nameof(ListSectors), new { id = created.Id }, created);
    }

    [HttpPatch("sectors/{id:guid}")]
    [RequirePermission("categories:edit")]
    [ProducesResponseType(typeof(AdminSectorDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateSector(Guid id, [FromBody] UpdateSectorRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        return Ok(await _service.UpdateSectorAsync(actingUserId, id, req, ct));
    }

    [HttpDelete("sectors/{id:guid}")]
    [RequirePermission("categories:delete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteSector(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        await _service.DeleteSectorAsync(actingUserId, id, ct);
        return NoContent();
    }

    [HttpPost("sectors/reorder")]
    [RequirePermission("categories:edit")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ReorderSectors([FromBody] ReorderRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        await _service.ReorderSectorsAsync(actingUserId, req, ct);
        return NoContent();
    }

    // ── Countries ────────────────────────────────────────────────────────────

    [HttpGet("countries")]
    [RequirePermission("categories:view")]
    [ProducesResponseType(typeof(IReadOnlyList<AdminCountryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListCountries(CancellationToken ct)
        => Ok(await _service.ListCountriesAsync(ct));

    [HttpPost("countries")]
    [RequirePermission("categories:create")]
    [ProducesResponseType(typeof(AdminCountryDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateCountry([FromBody] CreateCountryRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        var created = await _service.CreateCountryAsync(actingUserId, req, ct);
        return CreatedAtAction(nameof(ListCountries), new { id = created.Id }, created);
    }

    [HttpPatch("countries/{id:guid}")]
    [RequirePermission("categories:edit")]
    [ProducesResponseType(typeof(AdminCountryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateCountry(Guid id, [FromBody] UpdateCountryRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        return Ok(await _service.UpdateCountryAsync(actingUserId, id, req, ct));
    }

    [HttpDelete("countries/{id:guid}")]
    [RequirePermission("categories:delete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteCountry(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        await _service.DeleteCountryAsync(actingUserId, id, ct);
        return NoContent();
    }

    [HttpPost("countries/reorder")]
    [RequirePermission("categories:edit")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ReorderCountries([FromBody] ReorderRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        await _service.ReorderCountriesAsync(actingUserId, req, ct);
        return NoContent();
    }

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }
}
