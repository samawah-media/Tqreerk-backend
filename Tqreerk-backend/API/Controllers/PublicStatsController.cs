using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Taqreerk.Application.DTOs.Reports;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

/// Anonymous-readable platform stats for the public Landing page hero.
/// Tiny payload, indexed COUNTs, response-cached for 5 minutes — calling
/// this on every page load is essentially free.
[ApiController]
[Route("api/public/stats")]
[Produces("application/json")]
[AllowAnonymous]
public class PublicStatsController : ControllerBase
{
    private readonly IPublicReportService _reports;

    public PublicStatsController(IPublicReportService reports)
    {
        _reports = reports;
    }

    /// <summary>Counts that drive the homepage hero strip.</summary>
    [HttpGet("overview")]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Any)]
    [OutputCache(PolicyName = "PublicStats")]
    [ProducesResponseType(typeof(PublicStatsOverviewDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Overview(CancellationToken ct)
        => Ok(await _reports.GetPublicStatsAsync(ct));
}
