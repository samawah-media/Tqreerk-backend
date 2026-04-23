using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Auth;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Settings;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;
using Microsoft.Extensions.Options;

namespace Taqreerk.Application.Services;

public class AuthService : IAuthService
{
    private readonly TaqreerkDbContext _db;
    private readonly ITokenService _tokens;
    private readonly IRbacService _rbac;
    private readonly JwtSettings _jwt;

    public AuthService(TaqreerkDbContext db, ITokenService tokens, IRbacService rbac, IOptions<JwtSettings> jwt)
    {
        _db = db;
        _tokens = tokens;
        _rbac = rbac;
        _jwt = jwt.Value;
    }

    public async Task<AuthResult> RegisterIndividualAsync(RegisterIndividualRequest req, CancellationToken ct = default)
    {
        if (await _db.Users.AnyAsync(u => u.Email == req.Email, ct))
            throw new InvalidOperationException("Email already registered.");

        var user = new User
        {
            FullName = req.FullName,
            Email = req.Email.ToLowerInvariant(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
            Phone = req.Phone,
            JobTitle = req.JobTitle,
            InterestField = req.InterestField,
            CountryId = req.CountryId,
            PreferredLanguage = req.PreferredLanguage,
            UserType = "individual",
            Status = UserStatus.Active,
            EmailVerified = false,
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        return await IssueTokensAsync(user, null, null, ct);
    }

    public async Task<AuthResult> RegisterOrganizationAsync(RegisterOrganizationRequest req, CancellationToken ct = default)
    {
        if (await _db.Users.AnyAsync(u => u.Email == req.Email, ct))
            throw new InvalidOperationException("Email already registered.");

        var slug = GenerateSlug(req.NameEn);
        if (await _db.Organizations.AnyAsync(o => o.Slug == slug, ct))
            slug = $"{slug}-{Guid.NewGuid().ToString()[..8]}";

        var user = new User
        {
            FullName = req.NameEn,
            Email = req.Email.ToLowerInvariant(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
            Phone = req.Phone,
            UserType = "organization_admin",
            Status = UserStatus.Active,
            EmailVerified = false,
        };

        var organization = new Organization
        {
            NameAr = req.NameAr,
            NameEn = req.NameEn,
            Slug = slug,
            Type = req.Type,
            CountryId = req.CountryId,
            City = req.City,
            Phone = req.Phone,
            WebsiteUrl = req.WebsiteUrl,
            SectorScope = req.SectorScope,
            Status = OrganizationStatus.PendingReview,
        };

        var profile = new OrganizationProfile
        {
            CommercialRegisterNo = req.CommercialRegisterNo,
            IssuesReports = req.IssuesReports,
            AnnualReportsCount = req.AnnualReportsCount,
            WantsToPublish = req.WantsToPublish,
            InterestedInSubscription = req.InterestedInSubscription,
            ContactPersonName = req.ContactPersonName,
            ContactPersonTitle = req.ContactPersonTitle,
            ContactEmail = req.ContactEmail,
            PoliciesAccepted = req.PoliciesAccepted,
            PoliciesAcceptedAt = req.PoliciesAccepted ? DateTime.UtcNow : null,
        };

        organization.Profile = profile;

        _db.Users.Add(user);
        _db.Organizations.Add(organization);
        await _db.SaveChangesAsync(ct);

        // Add user as org admin
        var adminRole = await _db.Roles.FirstOrDefaultAsync(r => r.Name == "admin", ct)
            ?? throw new InvalidOperationException("Default roles not seeded.");

        _db.OrganizationMembers.Add(new OrganizationMember
        {
            UserId = user.Id,
            OrganizationId = organization.Id,
            RoleId = adminRole.Id,
        });
        await _db.SaveChangesAsync(ct);

        return await IssueTokensAsync(user, null, null, ct);
    }

    public async Task<AuthResult> LoginAsync(LoginRequest req, string? ipAddress, string? deviceInfo, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == req.Email.ToLowerInvariant(), ct)
            ?? throw new UnauthorizedAccessException("Invalid credentials.");

        if (!BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
            throw new UnauthorizedAccessException("Invalid credentials.");

        if (user.Status == UserStatus.Suspended)
            throw new UnauthorizedAccessException("Account is suspended.");

        return await IssueTokensAsync(user, ipAddress, deviceInfo, ct);
    }

    public async Task<AuthResult> RefreshAsync(string refreshToken, string? ipAddress, CancellationToken ct = default)
    {
        var tokenHash = HashToken(refreshToken);

        var stored = await _db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct)
            ?? throw new UnauthorizedAccessException("Invalid refresh token.");

        if (stored.IsRevoked)
            throw new UnauthorizedAccessException("Refresh token has been revoked.");

        if (stored.ExpiresAt < DateTime.UtcNow)
            throw new UnauthorizedAccessException("Refresh token has expired.");

        // Rotate: revoke old, issue new
        stored.IsRevoked = true;
        await _db.SaveChangesAsync(ct);

        return await IssueTokensAsync(stored.User, ipAddress, stored.DeviceInfo, ct);
    }

    public async Task RevokeTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        var tokenHash = HashToken(refreshToken);
        var stored = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);
        if (stored is null) return;

        stored.IsRevoked = true;
        await _db.SaveChangesAsync(ct);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<AuthResult> IssueTokensAsync(User user, string? ipAddress, string? deviceInfo, CancellationToken ct)
    {
        var rawRefreshToken = _tokens.GenerateRefreshToken();

        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = HashToken(rawRefreshToken),
            IpAddress = ipAddress,
            DeviceInfo = deviceInfo,
            ExpiresAt = DateTime.UtcNow.AddDays(_jwt.RefreshTokenExpiryDays),
        });
        await _db.SaveChangesAsync(ct);

        var roleNames = await _rbac.GetRoleNamesForUserAsync(user.Id, ct);
        var permissionKeys = await _rbac.GetPermissionKeysForUserAsync(user.Id, ct);

        var accessToken = _tokens.GenerateAccessToken(user, roleNames, permissionKeys);
        var expiresAt = DateTime.UtcNow.AddMinutes(_jwt.AccessTokenExpiryMinutes);

        var response = new AuthResponse(
            AccessToken: accessToken,
            ExpiresAt: expiresAt,
            User: new UserProfile(user.Id, user.FullName, user.Email, user.UserType, user.PreferredLanguage)
        );

        return new AuthResult(response, rawRefreshToken);
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string GenerateSlug(string name)
        => name.ToLowerInvariant()
               .Replace(" ", "-")
               .Replace("'", "")
               .Replace("\"", "");
}
