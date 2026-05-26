using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.API.Authorization;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

/// Admin dashboard rollups. Lives at /api/admin/dashboard/* and is open
/// to any platform staff member — every role needs to see queue badges,
/// so we don't gate by RBAC permission here.
[ApiController]
[Route("api/admin/dashboard")]
[Produces("application/json")]
[Authorize]
[RequirePlatformStaff]
public class AdminDashboardController : ControllerBase
{
    private readonly IAdminDashboardService _dashboard;

    public AdminDashboardController(IAdminDashboardService dashboard)
    {
        _dashboard = dashboard;
    }

    /// <summary>Counts that drive the admin topbar badges. Polled every
    /// 30 seconds by the SPA — keep it cheap.</summary>
    [HttpGet("quick-stats")]
    [ProducesResponseType(typeof(AdminQuickStatsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetQuickStats(CancellationToken ct)
        => Ok(await _dashboard.GetQuickStatsAsync(ct));
}
