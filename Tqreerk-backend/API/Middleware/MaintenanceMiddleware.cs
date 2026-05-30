using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Services;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.API.Middleware;

/// Short-circuits public traffic with HTTP 503 when the
/// `maintenance.enabled` system setting is true. Admin paths
/// (/api/admin/*), the public health probe (/healthz, /api/admin/health),
/// and Swagger stay reachable so SuperAdmins can lift the gate without
/// being locked out.
///
/// We resolve IAdminSettingsService per-request so the cached read uses
/// the same IMemoryCache instance everywhere — TTL is 30 seconds, so the
/// flag flip propagates to the middleware without a process restart.
public class MaintenanceMiddleware
{
    private static readonly string[] AlwaysAllowedPrefixes =
    [
        "/api/admin/",          // every admin endpoint, including the toggle itself
        "/api/auth/",           // SuperAdmin must be able to log in
        "/healthz",             // platform health probe
        "/swagger",             // dev tooling
        "/uploads",             // local-storage assets — keep working in dev
        "/admin/hangfire",      // staff job dashboard (JWT/cookie auth inside Hangfire)
    ];

    private readonly RequestDelegate _next;
    private readonly ILogger<MaintenanceMiddleware> _logger;

    public MaintenanceMiddleware(RequestDelegate next, ILogger<MaintenanceMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? string.Empty;
        if (AlwaysAllowedPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(ctx);
            return;
        }

        var settings = ctx.RequestServices.GetService<IAdminSettingsService>();
        if (settings is null)
        {
            // DI hasn't registered the service yet — fail open rather than
            // closed. Logging this loudly so it's caught in dev.
            _logger.LogWarning("MaintenanceMiddleware: IAdminSettingsService not registered; passing through.");
            await _next(ctx);
            return;
        }

        if (!await settings.IsMaintenanceModeAsync(ctx.RequestAborted))
        {
            await _next(ctx);
            return;
        }

        // 503 with a small JSON envelope. The SPA's axios interceptor
        // looks for `maintenance: true` and routes to the splash page.
        // We also try to surface the configured message (best-effort —
        // single round-trip via DbContext, no extra cache thrash).
        var message = await ReadMaintenanceMessageAsync(ctx);
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.Headers.RetryAfter = "300"; // hint to clients to back off 5 min
        await JsonSerializer.SerializeAsync(
            ctx.Response.Body,
            new
            {
                maintenance = true,
                message,
                title = "المنصة تحت الصيانة",
                status = StatusCodes.Status503ServiceUnavailable,
            },
            cancellationToken: ctx.RequestAborted);
    }

    private static async Task<string> ReadMaintenanceMessageAsync(HttpContext ctx)
    {
        try
        {
            using var scope = ctx.RequestServices.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TaqreerkDbContext>();
            var raw = await db.SystemSettings
                .AsNoTracking()
                .Where(s => s.Key == "maintenance.message")
                .Select(s => s.Value)
                .FirstOrDefaultAsync(ctx.RequestAborted);
            return string.IsNullOrWhiteSpace(raw)
                ? "المنصة تحت الصيانة، نعود قريبًا."
                : raw;
        }
        catch
        {
            return "المنصة تحت الصيانة، نعود قريبًا.";
        }
    }
}
