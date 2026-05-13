using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Taqreerk.Application.DTOs.Organizations;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

/// Public reference data (countries, sectors) used by signup/wizard dropdowns.
/// No auth required — these are static lookup tables.
[ApiController]
[Route("api")]
[Produces("application/json")]
[AllowAnonymous]
public class ReferenceController : ControllerBase
{
    private readonly IOrganizationService _orgs;

    public ReferenceController(IOrganizationService orgs) => _orgs = orgs;

    [HttpGet("countries")]
    [ResponseCache(Duration = 600)]
    [OutputCache(PolicyName = "Reference")]
    [ProducesResponseType(typeof(IReadOnlyList<CountryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCountries(CancellationToken ct)
        => Ok(await _orgs.ListCountriesAsync(ct));

    [HttpGet("sectors")]
    [ResponseCache(Duration = 600)]
    [OutputCache(PolicyName = "Reference")]
    [ProducesResponseType(typeof(IReadOnlyList<SectorDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSectors(CancellationToken ct)
        => Ok(await _orgs.ListSectorsAsync(ct));
}
