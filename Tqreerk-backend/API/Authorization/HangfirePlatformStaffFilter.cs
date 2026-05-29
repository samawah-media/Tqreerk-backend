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
/// Hangfire dashboard auth filter. Accepts the platform JWT from either:
///   - Authorization: Bearer &lt;token&gt; header
///   - ?access_token=&lt;token&gt; query parameter (browser navigation)
/// Validates the token and checks IsPlatformStaff in the DB.
/// </summary>
public sealed class HangfirePlatformStaffFilter(IServiceProvider services) : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var http = context.GetHttpContext();

        var token = ExtractToken(http);
        if (token is null) return false;

        var jwt = ValidateToken(token);
        if (jwt is null) return false;

        var sub = jwt.FindFirstValue(JwtRegisteredClaimNames.Sub)
               ?? jwt.FindFirstValue(ClaimTypes.NameIdentifier);
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
        var header = http.Request.Headers.Authorization.FirstOrDefault();
        if (header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return header["Bearer ".Length..].Trim();

        if (http.Request.Query.TryGetValue("access_token", out var qp) && !string.IsNullOrWhiteSpace(qp))
            return qp.ToString();

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
