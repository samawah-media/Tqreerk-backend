using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Hangfire.Dashboard;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Taqreerk.Application.Settings;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.API.Authorization;

/// <summary>
/// Hangfire dashboard auth. Token sources (first match wins):
///   - Authorization: Bearer header
///   - ?access_token= query (initial browser login; also sets a session cookie)
///   - hangfire_auth cookie (sub-pages, css/js after first login)
/// Validates JWT and requires IsPlatformStaff in the database.
/// </summary>
public sealed class HangfirePlatformStaffFilter(IServiceProvider services) : IDashboardAuthorizationFilter
{
    public const string AuthCookieName = "hangfire_auth";
    private static readonly TimeSpan CookieLifetime = TimeSpan.FromHours(8);

    public bool Authorize(DashboardContext context)
    {
        var http = context.GetHttpContext();

        // Hangfire loads many relative css/js URLs without ?access_token=.
        if (IsHangfireStaticAsset(http.Request.Path))
            return true;

        var token = ExtractToken(http);
        if (token is null)
            return false;

        var jwt = ValidateToken(token);
        if (jwt is null)
            return false;

        var sub = jwt.FindFirstValue(JwtRegisteredClaimNames.Sub)
               ?? jwt.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(sub, out var userId))
            return false;

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaqreerkDbContext>();
        var isStaff = db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.IsPlatformStaff)
            .FirstOrDefault();

        if (isStaff)
            TryPersistAuthCookie(http, token);

        return isStaff;
    }

    private static bool IsHangfireStaticAsset(PathString path)
    {
        var p = path.Value ?? string.Empty;
        return p.Contains("/css/", StringComparison.OrdinalIgnoreCase)
               || p.Contains("/js/", StringComparison.OrdinalIgnoreCase)
               || p.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase)
               || p.EndsWith(".woff", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryPersistAuthCookie(HttpContext http, string token)
    {
        if (http.Response.HasStarted)
            return;

        var fromQuery = http.Request.Query.ContainsKey("access_token");
        var fromBearer = http.Request.Headers.Authorization.FirstOrDefault() is not null
            && http.Request.Headers.Authorization.ToString()
                .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
        if (!fromQuery && !fromBearer)
            return;

        if (http.Request.Cookies.TryGetValue(AuthCookieName, out var existing)
            && string.Equals(existing, token, StringComparison.Ordinal))
        {
            return;
        }

        http.Response.Cookies.Append(AuthCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/admin/hangfire",
            MaxAge = CookieLifetime,
        });
    }

    private static string? ExtractToken(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.FirstOrDefault();
        if (header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header["Bearer ".Length..].Trim();

        if (http.Request.Query.TryGetValue("access_token", out var qp) && !string.IsNullOrWhiteSpace(qp))
            return qp.ToString();

        if (http.Request.Cookies.TryGetValue(AuthCookieName, out var cookie)
            && !string.IsNullOrWhiteSpace(cookie))
        {
            return cookie;
        }

        return null;
    }

    private ClaimsPrincipal? ValidateToken(string token)
    {
        var jwt = services.GetRequiredService<IOptions<JwtSettings>>().Value;
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SecretKey)),
        };

        try
        {
            return new JwtSecurityTokenHandler().ValidateToken(token, parameters, out _);
        }
        catch
        {
            return null;
        }
    }
}
