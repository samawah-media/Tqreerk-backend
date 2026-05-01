using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.API.Authorization;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

/// One-stop platform stats. Single endpoint — every chart and KPI on the
/// admin Dashboard reads from the same payload, so a refresh is one
/// round-trip instead of fifteen. Five-minute response cache means a
/// burst of admins hitting the page only pays the query cost once.
[ApiController]
[Route("api/admin/stats")]
[Produces("application/json")]
[Authorize]
[RequirePlatformStaff]
public class AdminStatsController : ControllerBase
{
    private readonly IAdminStatsService _stats;

    public AdminStatsController(IAdminStatsService stats)
    {
        _stats = stats;
    }

    /// <summary>Comprehensive platform stats: KPIs, top-N lists, daily
    /// timeseries, breakdowns, and recent rejections — all in one call.</summary>
    [HttpGet("overview")]
    [RequirePermission("dashboard:view")]
    [ResponseCache(Duration = 300)] // 5 minutes
    [ProducesResponseType(typeof(AdminStatsOverviewDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetOverview(CancellationToken ct)
        => Ok(await _stats.GetOverviewAsync(ct));
}
