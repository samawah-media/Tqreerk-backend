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
/// Hangfire dashboard auth filter. Accepts the platform JWT from:
///   - ?access_token=&lt;token&gt; query parameter (browser navigation)
///   - Authorization: Bearer &lt;token&gt; header
///   - hangfire_auth cookie (set automatically after first token auth so
///     CSS/JS sub-resource requests pass without re-appending the query param)
/// Validates the token and checks IsPlatformStaff in the DB.
/// </summary>
public sealed class HangfirePlatformStaffFilter(IServiceProvider services) : IDashboardAuthorizationFilter
{
    private const string CookieName = "hangfire_auth";

    public bool Authorize(DashboardContext context)
    {
        var http = context.GetHttpContext();

        // Cookie path: set on first token auth; carries subsequent asset requests.
        if (http.Request.Cookies.TryGetValue(CookieName, out var cookie)
            && !string.IsNullOrWhiteSpace(cookie))
        {
            var cookiePrincipal = ValidateToken(cookie);
            if (cookiePrincipal is not null && IsStaff(cookiePrincipal))
                return true;
        }

        // Token path: Authorization header or ?access_token query param.
        var token = ExtractToken(http);
        if (token is null) return false;

        var principal = ValidateToken(token);
        if (principal is null) return false;

        if (!IsStaff(principal)) return false;

        // Write a short-lived cookie so CSS/JS sub-requests don't need the token.
        http.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = DateTimeOffset.UtcNow.AddHours(8),
        });

        return true;
    }

    private bool IsStaff(ClaimsPrincipal principal)
    {
        var sub = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
               ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(sub, out var userId)) return false;

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaqreerkDbContext>();
        return db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.IsPlatformStaff)
            .FirstOrDefault();
    }

    private static string? ExtractToken(HttpContext http)
    {
        if (http.Request.Query.TryGetValue("access_token", out var qp) && !string.IsNullOrWhiteSpace(qp))
            return qp.ToString();

        var header = http.Request.Headers.Authorization.FirstOrDefault();
        if (header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header["Bearer ".Length..].Trim();

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
