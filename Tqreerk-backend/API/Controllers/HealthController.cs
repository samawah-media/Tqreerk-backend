using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.API.Controllers;

[ApiController]
[Route("healthz")]
public class HealthController(TaqreerkDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        try
        {
            var canConnect = await db.Database.CanConnectAsync();
            var pendingMigrations = (await db.Database.GetPendingMigrationsAsync()).ToList();

            if (!canConnect)
                return StatusCode(503, new { status = "unhealthy", database = "unreachable" });

            if (pendingMigrations.Count > 0)
                return StatusCode(503, new { status = "unhealthy", database = "connected", pendingMigrations });

            return Ok(new { status = "healthy", database = "connected", pendingMigrations = Array.Empty<string>() });
        }
        catch (Exception ex)
        {
            return StatusCode(503, new { status = "unhealthy", error = ex.Message });
        }
    }
}
