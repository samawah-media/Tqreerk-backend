using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taqreerk.Application.DTOs.Auth;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Settings;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class AuthService : IAuthService
{
    private readonly TaqreerkDbContext _db;
    private readonly ITokenService _tokens;
    private readonly IRbacService _rbac;
    private readonly IEmailSender _email;
    private readonly ITwoFactorService _twoFactor;
    private readonly ISubscriptionProvisioningService _subscriptions;
    private readonly JwtSettings _jwt;
    private readonly EmailSettings _emailSettings;

    public AuthService(
        TaqreerkDbContext db,
        ITokenService tokens,
        IRbacService rbac,
        IEmailSender email,
        ITwoFactorService twoFactor,
        ISubscriptionProvisioningService subscriptions,
        IOptions<JwtSettings> jwt,
        IOptions<EmailSettings> emailSettings)
    {
        _db = db;
        _tokens = tokens;
        _rbac = rbac;
        _email = email;
        _twoFactor = twoFactor;
        _subscriptions = subscriptions;
        _jwt = jwt.Value;
        _emailSettings = emailSettings.Value;
    }

    public async Task<string> RegisterIndividualAsync(RegisterIndividualRequest req, CancellationToken ct = default)
    {
        var normalized = req.Email.ToLowerInvariant();

        if (await _db.Users.AnyAsync(u => u.Email == normalized, ct))
            throw new InvalidOperationException("Email already registered.");

        // Remove any previous pending registration for this email so the user can retry.
        var old = await _db.PendingRegistrations.Where(p => p.Email == normalized).ToListAsync(ct);
        _db.PendingRegistrations.RemoveRange(old);

        var code = GenerateNumericCode(OtpCodeLength);
        var pending = new PendingRegistration
        {
            Email = normalized,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
            FullName = req.FullName,
            Phone = req.Phone,
            JobTitle = req.JobTitle,
            InterestField = req.InterestField,
            CountryId = req.CountryId,
            PreferredLanguage = req.PreferredLanguage,
            UserType = "individual",
            OtpHash = HashToken(code),
            OtpExpiresAt = DateTime.UtcNow.Add(OtpLifetime),
        };

        _db.PendingRegistrations.Add(pending);
        await _db.SaveChangesAsync(ct);

        await SendOtpEmailAsync(req.FullName, normalized, code, ct);

        return normalized;
    }

    public async Task<string> RegisterOrganizationAsync(RegisterOrganizationRequest req, CancellationToken ct = default)
    {
        var normalized = req.Email.ToLowerInvariant();

        await ValidateOrganizationPlanIdAsync(req.PlanId, ct);

        if (await _db.Users.AnyAsync(u => u.Email == normalized, ct))
            throw new InvalidOperationException("Email already registered.");

        var old = await _db.PendingRegistrations.Where(p => p.Email == normalized).ToListAsync(ct);
        _db.PendingRegistrations.RemoveRange(old);

        var code = GenerateNumericCode(OtpCodeLength);
        var pending = new PendingRegistration
        {
            Email = normalized,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
            FullName = req.NameEn,
            Phone = req.Phone,
            UserType = "organization_admin",
            OrgNameAr = req.NameAr,
            OrgNameEn = req.NameEn,
            OrgType = req.Type,
            CountryId = req.CountryId,
            OrgCity = req.City,
            OrgWebsiteUrl = req.WebsiteUrl,
            OrgSectorScope = req.SectorScope,
            OrgCommercialRegisterNo = req.CommercialRegisterNo,
            OrgCommercialRegisterName = req.CommercialRegisterName,
            OrgCommercialRegisterExpiryDate = AsUtcDate(req.CommercialRegisterExpiryDate),
            OrgTaxNumber = req.TaxNumber,
            OrgLicenseDocumentUrl = req.LicenseDocumentUrl,
            OrgEmployeeCount = req.EmployeeCount,
            OrgIssuesReports = req.IssuesReports,
            OrgAnnualReportsCount = req.AnnualReportsCount,
            OrgWantsToPublish = req.WantsToPublish,
            OrgInterestedInSubscription = req.InterestedInSubscription,
            OrgContactPersonName = req.ContactPersonName,
            OrgContactPersonTitle = req.ContactPersonTitle,
            OrgContactEmail = req.ContactEmail,
            OrgPoliciesAccepted = req.PoliciesAccepted,
            OtpHash = HashToken(code),
            OtpExpiresAt = DateTime.UtcNow.Add(OtpLifetime),
        };

        _db.PendingRegistrations.Add(pending);
        await _db.SaveChangesAsync(ct);

        await SendOtpEmailAsync(req.NameEn, normalized, code, ct);

        return normalized;
    }

    private async Task SendOtpEmailAsync(string fullName, string email, string code, CancellationToken ct)
    {
        var minutes = (int)OtpLifetime.TotalMinutes;
        var body = $"<p>Hello {System.Net.WebUtility.HtmlEncode(fullName)},</p>" +
                   $"<p>Your Taqreerk verification code is:</p>" +
                   $"<p style=\"font-size:24px;font-weight:bold;letter-spacing:4px\">{code}</p>" +
                   $"<p>This code expires in {minutes} minutes.</p>";

        await _email.SendEmailAsync(email, "Your Taqreerk verification code", body, ct);
    }

    private const int MaxFailedLoginAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    /// True when the user belongs to at least one organization that is
    /// currently suspended. Used to block login until the admin lifts the
    /// suspension. We only care about active memberships — historical/
    /// deactivated rows shouldn't keep the user out of the platform.
    private async Task<bool> IsMemberOfSuspendedOrgAsync(Guid userId, CancellationToken ct)
        => await _db.OrganizationMembers
            .AsNoTracking()
            .AnyAsync(m => m.UserId == userId
                        && m.IsActive
                        && m.Organization.Status == OrganizationStatus.Suspended, ct);

    public async Task<AuthResult> LoginAsync(LoginRequest req, string? ipAddress, string? deviceInfo, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == req.Email.ToLowerInvariant(), ct)
            ?? throw new UnauthorizedAccessException("Invalid credentials.");

        // Locked out? Reject before checking the password so timing can't reveal lockout state.
        if (user.LockoutEndsAt is { } lockedUntil && lockedUntil > DateTime.UtcNow)
        {
            var minutes = (int)Math.Ceiling((lockedUntil - DateTime.UtcNow).TotalMinutes);
            throw new UnauthorizedAccessException(
                $"Too many failed attempts. Try again in {minutes} minute(s).");
        }

        if (!BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
        {
            user.FailedLoginAttempts += 1;
            if (user.FailedLoginAttempts >= MaxFailedLoginAttempts)
            {
                user.LockoutEndsAt = DateTime.UtcNow.Add(LockoutDuration);
                user.FailedLoginAttempts = 0; // reset counter; lockout window takes over
            }
            await _db.SaveChangesAsync(ct);
            throw new UnauthorizedAccessException("Invalid credentials.");
        }

        if (user.Status == UserStatus.Suspended)
            throw new UnauthorizedAccessException("Account is suspended.");

        // An org member whose org is suspended can't log in either. Platform
        // staff are exempt — they need to keep working even if some org they
        // (rarely) belong to is suspended. The check is a single indexed
        // join against organization_members + organizations.
        if (!user.IsPlatformStaff && await IsMemberOfSuspendedOrgAsync(user.Id, ct))
            throw new UnauthorizedAccessException(
                "Your organization is currently suspended. Contact platform support.");

        // Successful login clears the failure counter and any expired lockout.
        if (user.FailedLoginAttempts != 0 || user.LockoutEndsAt is not null)
        {
            user.FailedLoginAttempts = 0;
            user.LockoutEndsAt = null;
            await _db.SaveChangesAsync(ct);
        }

        return await IssueTokensAsync(user, ipAddress, deviceInfo, ct);
    }

    public async Task<LoginOutcome> LoginWithTwoFactorAsync(
        LoginRequest req, string? ipAddress, string? deviceInfo, CancellationToken ct = default)
    {
        // Reuse the password + lockout flow from LoginAsync. We can't just
        // call it directly because the regular path issues tokens at the
        // end — so we duplicate the verification, then branch.
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == req.Email.ToLowerInvariant(), ct)
            ?? throw new UnauthorizedAccessException("Invalid credentials.");

        if (user.LockoutEndsAt is { } lockedUntil && lockedUntil > DateTime.UtcNow)
        {
            var minutes = (int)Math.Ceiling((lockedUntil - DateTime.UtcNow).TotalMinutes);
            throw new UnauthorizedAccessException(
                $"Too many failed attempts. Try again in {minutes} minute(s).");
        }

        if (!BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
        {
            user.FailedLoginAttempts += 1;
            if (user.FailedLoginAttempts >= MaxFailedLoginAttempts)
            {
                user.LockoutEndsAt = DateTime.UtcNow.Add(LockoutDuration);
                user.FailedLoginAttempts = 0;
            }
            await _db.SaveChangesAsync(ct);
            throw new UnauthorizedAccessException("Invalid credentials.");
        }

        if (user.Status == UserStatus.Suspended)
            throw new UnauthorizedAccessException("Account is suspended.");

        // An org member whose org is suspended can't log in either. Platform
        // staff are exempt — they need to keep working even if some org they
        // (rarely) belong to is suspended. The check is a single indexed
        // join against organization_members + organizations.
        if (!user.IsPlatformStaff && await IsMemberOfSuspendedOrgAsync(user.Id, ct))
            throw new UnauthorizedAccessException(
                "Your organization is currently suspended. Contact platform support.");

        if (user.FailedLoginAttempts != 0 || user.LockoutEndsAt is not null)
        {
            user.FailedLoginAttempts = 0;
            user.LockoutEndsAt = null;
            await _db.SaveChangesAsync(ct);
        }

        // Branch: only platform staff are gated by 2FA. Everyone else logs
        // in normally. Within staff, the gate fires only when 2FA is
        // ALREADY active — staff who haven't set up yet get a normal
        // token pair plus a flag in /me telling the SPA to push them
        // through the setup wizard before they can do anything.
        if (user.IsPlatformStaff
            && await _twoFactor.RequiresVerificationAsync(user.Id, ct))
        {
            var challenge = _tokens.GenerateTwoFactorChallengeToken(user.Id);
            return new LoginOutcome(
                Tokens: null,
                TwoFactorChallenge: new TwoFactorChallengeResult(challenge, user.Email));
        }

        var tokens = await IssueTokensAsync(user, ipAddress, deviceInfo, ct);
        return new LoginOutcome(Tokens: tokens, TwoFactorChallenge: null);
    }

    public async Task<AuthResult> CompleteTwoFactorLoginAsync(
        string challengeToken, string code, string? ipAddress, string? deviceInfo, CancellationToken ct = default)
    {
        var userId = _tokens.ValidateTwoFactorChallengeToken(challengeToken)
            ?? throw new UnauthorizedAccessException("Invalid or expired 2FA challenge.");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new UnauthorizedAccessException("User not found.");

        if (user.Status == UserStatus.Suspended)
            throw new UnauthorizedAccessException("Account is suspended.");

        // An org member whose org is suspended can't log in either. Platform
        // staff are exempt — they need to keep working even if some org they
        // (rarely) belong to is suspended. The check is a single indexed
        // join against organization_members + organizations.
        if (!user.IsPlatformStaff && await IsMemberOfSuspendedOrgAsync(user.Id, ct))
            throw new UnauthorizedAccessException(
                "Your organization is currently suspended. Contact platform support.");

        // Defence in depth — if 2FA somehow got disabled between step 1
        // and step 2 (admin reset?) we don't accept the challenge.
        if (!await _twoFactor.RequiresVerificationAsync(userId, ct))
            throw new UnauthorizedAccessException("2FA is not active for this account.");

        if (!await _twoFactor.VerifyAsync(userId, code, ct))
            throw new UnauthorizedAccessException("Invalid verification code.");

        return await IssueTokensAsync(user, ipAddress, deviceInfo, ct);
    }

    public async Task<IReadOnlyList<SessionDto>> GetActiveSessionsAsync(Guid userId, string? currentRefreshToken, CancellationToken ct = default)
    {
        var currentHash = string.IsNullOrWhiteSpace(currentRefreshToken) ? null : HashToken(currentRefreshToken);
        var now = DateTime.UtcNow;

        var rows = await _db.RefreshTokens
            .AsNoTracking()
            .Where(t => t.UserId == userId && !t.IsRevoked && t.ExpiresAt > now)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);

        return rows
            .Select(t => new SessionDto(
                t.Id,
                t.DeviceInfo,
                t.IpAddress,
                t.CreatedAt,
                t.ExpiresAt,
                currentHash is not null && t.TokenHash == currentHash))
            .ToList();
    }

    public async Task RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        var session = await _db.RefreshTokens
            .FirstOrDefaultAsync(t => t.Id == sessionId && t.UserId == userId, ct)
            ?? throw new KeyNotFoundException("Session not found.");

        if (session.IsRevoked) return;

        session.IsRevoked = true;
        await _db.SaveChangesAsync(ct);
    }

    public async Task RevokeAllSessionsAsync(Guid userId, CancellationToken ct = default)
    {
        var sessions = await _db.RefreshTokens
            .Where(t => t.UserId == userId && !t.IsRevoked)
            .ToListAsync(ct);

        foreach (var s in sessions) s.IsRevoked = true;
        if (sessions.Count > 0) await _db.SaveChangesAsync(ct);
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

    // ── Email verification ──────────────────────────────────────────────────

    public async Task SendVerificationEmailAsync(string email, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email.ToLowerInvariant(), ct);

        // Silent success on unknown address to avoid enumeration.
        if (user is null || user.EmailVerified) return;

        // Invalidate any outstanding tokens for this user.
        var existing = await _db.EmailVerificationTokens
            .Where(t => t.UserId == user.Id && t.UsedAt == null)
            .ToListAsync(ct);
        foreach (var t in existing) t.UsedAt = DateTime.UtcNow;

        var rawToken = GenerateToken();
        _db.EmailVerificationTokens.Add(new EmailVerificationToken
        {
            UserId = user.Id,
            TokenHash = HashToken(rawToken),
            ExpiresAt = DateTime.UtcNow.AddHours(_emailSettings.VerificationTokenHoursValid),
        });

        await _db.SaveChangesAsync(ct);

        var link = $"{_emailSettings.AppBaseUrl.TrimEnd('/')}/verify-email?token={Uri.EscapeDataString(rawToken)}";
        var body = $"<p>Hello {System.Net.WebUtility.HtmlEncode(user.FullName)},</p>" +
                   $"<p>Click the link below to verify your email address. The link expires in {_emailSettings.VerificationTokenHoursValid} hours.</p>" +
                   $"<p><a href=\"{link}\">{link}</a></p>";

        await _email.SendEmailAsync(user.Email, "Verify your Taqreerk email", body, ct);
    }

    // ── Email OTP (6-digit numeric code) ────────────────────────────────────

    private const int OtpCodeLength = 6;
    private static readonly TimeSpan OtpLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OtpResendCooldown = TimeSpan.FromSeconds(60);

    public async Task SendEmailOtpAsync(string email, CancellationToken ct = default)
    {
        var normalized = email.ToLowerInvariant();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalized, ct);

        // Silent success on unknown address to avoid enumeration.
        if (user is null) return;

        if (user.EmailVerified)
            throw new InvalidOperationException("Email is already verified.");

        // Rate-limit: refuse a new send if the most recent unused token is younger than the cooldown.
        var latest = await _db.EmailVerificationTokens
            .Where(t => t.UserId == user.Id && t.UsedAt == null)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (latest is not null && DateTime.UtcNow - latest.CreatedAt < OtpResendCooldown)
        {
            var wait = (int)Math.Ceiling((OtpResendCooldown - (DateTime.UtcNow - latest.CreatedAt)).TotalSeconds);
            throw new InvalidOperationException($"Please wait {wait} seconds before requesting a new code.");
        }

        // Invalidate any outstanding tokens.
        var existing = await _db.EmailVerificationTokens
            .Where(t => t.UserId == user.Id && t.UsedAt == null)
            .ToListAsync(ct);
        foreach (var t in existing) t.UsedAt = DateTime.UtcNow;

        var code = GenerateNumericCode(OtpCodeLength);
        _db.EmailVerificationTokens.Add(new EmailVerificationToken
        {
            UserId = user.Id,
            TokenHash = HashToken(code),
            ExpiresAt = DateTime.UtcNow.Add(OtpLifetime),
        });

        await _db.SaveChangesAsync(ct);

        var minutes = (int)OtpLifetime.TotalMinutes;
        var body = $"<p>Hello {System.Net.WebUtility.HtmlEncode(user.FullName)},</p>" +
                   $"<p>Your Taqreerk verification code is:</p>" +
                   $"<p style=\"font-size:24px;font-weight:bold;letter-spacing:4px\">{code}</p>" +
                   $"<p>This code expires in {minutes} minutes.</p>";

        await _email.SendEmailAsync(user.Email, "Your Taqreerk verification code", body, ct);
    }

    public async Task<AuthResult> VerifyEmailOtpAsync(
        string email,
        string code,
        string? ipAddress,
        string? deviceInfo,
        Guid? organizationPlanId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != OtpCodeLength || !code.All(char.IsDigit))
            throw new ArgumentException("Invalid verification code.");

        var normalized = email.ToLowerInvariant();

        // Check if this is a pending registration (new user) or an existing user requesting re-verify.
        var pending = await _db.PendingRegistrations
            .FirstOrDefaultAsync(p => p.Email == normalized, ct);

        if (pending is not null)
        {
            if (pending.OtpHash != HashToken(code))
                throw new UnauthorizedAccessException("Invalid verification code.");

            if (pending.OtpExpiresAt < DateTime.UtcNow)
                throw new UnauthorizedAccessException("Verification code has expired.");

            return await PromotePendingToUserAsync(pending, ipAddress, deviceInfo, organizationPlanId, ct);
        }

        // Existing user re-verifying (e.g. resend OTP flow).
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalized, ct)
            ?? throw new UnauthorizedAccessException("Invalid verification code.");

        var hash = HashToken(code);
        var record = await _db.EmailVerificationTokens
            .Where(t => t.UserId == user.Id && t.TokenHash == hash)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(ct)
            ?? throw new UnauthorizedAccessException("Invalid verification code.");

        if (record.UsedAt is not null)
            throw new UnauthorizedAccessException("Verification code has already been used.");

        if (record.ExpiresAt < DateTime.UtcNow)
            throw new UnauthorizedAccessException("Verification code has expired.");

        record.UsedAt = DateTime.UtcNow;
        user.EmailVerified = true;

        await _db.SaveChangesAsync(ct);

        return await IssueTokensAsync(user, ipAddress, deviceInfo, ct);
    }

    private async Task<AuthResult> PromotePendingToUserAsync(
        PendingRegistration pending,
        string? ipAddress,
        string? deviceInfo,
        Guid? organizationPlanId,
        CancellationToken ct)
    {
        var user = new User
        {
            Email = pending.Email,
            PasswordHash = pending.PasswordHash,
            FullName = pending.FullName,
            Phone = pending.Phone,
            UserType = pending.UserType,
            JobTitle = pending.JobTitle,
            InterestField = pending.InterestField,
            CountryId = pending.CountryId,
            PreferredLanguage = pending.PreferredLanguage,
            Status = UserStatus.Active,
            EmailVerified = true,
        };

        _db.Users.Add(user);
        _db.PendingRegistrations.Remove(pending);
        await _db.SaveChangesAsync(ct);

        if (pending.UserType == "organization_admin")
        {
            var slug = GenerateSlug(pending.OrgNameEn ?? pending.FullName);
            if (await _db.Organizations.AnyAsync(o => o.Slug == slug, ct))
                slug = $"{slug}-{Guid.NewGuid().ToString()[..8]}";

            var organization = new Organization
            {
                NameAr = pending.OrgNameAr ?? pending.FullName,
                NameEn = pending.OrgNameEn ?? pending.FullName,
                Slug = slug,
                Type = pending.OrgType ?? OrganizationType.Governmental,
                CountryId = pending.CountryId,
                City = pending.OrgCity,
                Phone = pending.Phone,
                WebsiteUrl = pending.OrgWebsiteUrl,
                SectorScope = pending.OrgSectorScope,
                Status = OrganizationStatus.PendingReview,
                CreatedByUserId = user.Id,
                Profile = new OrganizationProfile
                {
                    CommercialRegisterNo = pending.OrgCommercialRegisterNo,
                    CommercialRegisterName = pending.OrgCommercialRegisterName,
                    CommercialRegisterExpiryDate = AsUtcDate(pending.OrgCommercialRegisterExpiryDate),
                    TaxNumber = pending.OrgTaxNumber,
                    LicenseDocumentUrl = pending.OrgLicenseDocumentUrl,
                    EmployeeCount = pending.OrgEmployeeCount,
                    IssuesReports = pending.OrgIssuesReports,
                    AnnualReportsCount = pending.OrgAnnualReportsCount ?? 0,
                    WantsToPublish = pending.OrgWantsToPublish,
                    InterestedInSubscription = pending.OrgInterestedInSubscription,
                    ContactPersonName = pending.OrgContactPersonName,
                    ContactPersonTitle = pending.OrgContactPersonTitle,
                    ContactEmail = pending.OrgContactEmail,
                    PoliciesAccepted = pending.OrgPoliciesAccepted,
                    PoliciesAcceptedAt = pending.OrgPoliciesAccepted ? DateTime.UtcNow : null,
                },
            };

            _db.Organizations.Add(organization);
            await _db.SaveChangesAsync(ct);

            var adminRole = await _db.Roles.FirstOrDefaultAsync(r => r.Name == "admin", ct)
                ?? throw new InvalidOperationException("Default roles not seeded.");

            _db.OrganizationMembers.Add(new OrganizationMember
            {
                UserId = user.Id,
                OrganizationId = organization.Id,
                RoleId = adminRole.Id,
            });
            await _db.SaveChangesAsync(ct);

            if (!organizationPlanId.HasValue)
            {
                throw new InvalidOperationException(
                    "Organization plan is required to complete institution registration.");
            }

            await ValidateOrganizationPlanIdAsync(organizationPlanId.Value, ct);
            await _subscriptions.EnsureOrganizationPlanAsync(
                organization.Id,
                organizationPlanId.Value,
                awaitingPayment: true,
                ct);

            await NotifyPlatformStaffOfPendingOrganizationAsync(organization, user, ct);
        }
        else
        {
            await _subscriptions.EnsureIndividualFreeAsync(user.Id, ct);
        }

        return await IssueTokensAsync(user, ipAddress, deviceInfo, ct);
    }

    private async Task NotifyPlatformStaffOfPendingOrganizationAsync(
        Organization organization, User founder, CancellationToken ct)
    {
        var staffEmails = await _db.Users
            .AsNoTracking()
            .Where(u => u.IsPlatformStaff && u.Status == UserStatus.Active)
            .Select(u => u.Email)
            .Distinct()
            .ToListAsync(ct);

        if (staffEmails.Count == 0) return;

        var adminBase = _emailSettings.AppBaseUrl.TrimEnd('/');
        var reviewUrl = $"{adminBase}/organizations/{organization.Id}";
        var body =
            "<p>طلب تسجيل جهة جديد يحتاج مراجعة.</p>" +
            $"<p><strong>{System.Net.WebUtility.HtmlEncode(organization.NameAr)}</strong> " +
            $"({System.Net.WebUtility.HtmlEncode(organization.NameEn)})</p>" +
            $"<p>المؤسس: {System.Net.WebUtility.HtmlEncode(founder.FullName)} — " +
            $"{System.Net.WebUtility.HtmlEncode(founder.Email)}</p>" +
            $"<p><a href=\"{reviewUrl}\">مراجعة الطلب في لوحة الإدارة</a></p>";

        foreach (var email in staffEmails)
        {
            try
            {
                await _email.SendEmailAsync(
                    email,
                    "طلب تسجيل جهة جديد — بانتظار المراجعة",
                    body,
                    ct);
            }
            catch
            {
                // Best-effort — org is already persisted; admin can still see it in the list.
            }
        }
    }

    private async Task ValidateOrganizationPlanIdAsync(Guid planId, CancellationToken ct)
    {
        var valid = await _db.Plans.AnyAsync(
            p => p.Id == planId
              && p.IsActive
              && p.TargetType == PlanTargetType.Organization,
            ct);

        if (!valid)
            throw new InvalidOperationException("Invalid or inactive organization plan.");
    }

    private static string GenerateNumericCode(int length)
    {
        // Cryptographically random uniform digits (avoid modulo bias from raw bytes).
        var digits = new char[length];
        Span<byte> buffer = stackalloc byte[4];
        for (var i = 0; i < length; i++)
        {
            RandomNumberGenerator.Fill(buffer);
            var value = BitConverter.ToUInt32(buffer) % 10;
            digits[i] = (char)('0' + value);
        }
        return new string(digits);
    }

    public async Task ConfirmEmailAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Token is required.");

        var hash = HashToken(token);
        var record = await _db.EmailVerificationTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct)
            ?? throw new UnauthorizedAccessException("Invalid verification token.");

        if (record.UsedAt is not null)
            throw new UnauthorizedAccessException("Verification token has already been used.");

        if (record.ExpiresAt < DateTime.UtcNow)
            throw new UnauthorizedAccessException("Verification token has expired.");

        record.UsedAt = DateTime.UtcNow;
        record.User.EmailVerified = true;

        await _db.SaveChangesAsync(ct);
    }

    // ── Password reset ──────────────────────────────────────────────────────

    public async Task SendPasswordResetOtpAsync(string email, string? ipAddress, CancellationToken ct = default)
    {
        var normalized = email.ToLowerInvariant().Trim();
        var user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == normalized && u.EmailVerified, ct);

        // Silent success — don't reveal whether the email exists or is verified.
        if (user is null) return;

        var latest = await _db.PasswordResetTokens
            .Where(t => t.UserId == user.Id && t.UsedAt == null)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (latest is not null && DateTime.UtcNow - latest.CreatedAt < OtpResendCooldown)
        {
            var wait = (int)Math.Ceiling((OtpResendCooldown - (DateTime.UtcNow - latest.CreatedAt)).TotalSeconds);
            throw new InvalidOperationException($"Please wait {wait} seconds before requesting a new code.");
        }

        var existing = await _db.PasswordResetTokens
            .Where(t => t.UserId == user.Id && t.UsedAt == null)
            .ToListAsync(ct);
        foreach (var t in existing) t.UsedAt = DateTime.UtcNow;

        var code = GenerateNumericCode(OtpCodeLength);
        _db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = HashToken(code),
            IpAddress = ipAddress,
            ExpiresAt = DateTime.UtcNow.Add(OtpLifetime),
        });

        await _db.SaveChangesAsync(ct);

        var minutes = (int)OtpLifetime.TotalMinutes;
        var body = $"<p>Hello {System.Net.WebUtility.HtmlEncode(user.FullName)},</p>" +
                   $"<p>Your Taqreerk password reset code is:</p>" +
                   $"<p style=\"font-size:24px;font-weight:bold;letter-spacing:4px\">{code}</p>" +
                   $"<p>This code expires in {minutes} minutes.</p>" +
                   "<p>If you did not request a password reset, you can safely ignore this email.</p>";

        await _email.SendEmailAsync(user.Email, "Reset your Taqreerk password", body, ct);
    }

    public async Task<string> VerifyPasswordResetOtpAsync(string email, string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != OtpCodeLength || !code.All(char.IsDigit))
            throw new ArgumentException("Invalid verification code.");

        var normalized = email.ToLowerInvariant().Trim();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalized && u.EmailVerified, ct)
            ?? throw new UnauthorizedAccessException("Invalid verification code.");

        var hash = HashToken(code);
        var otpRecord = await _db.PasswordResetTokens
            .Where(t => t.UserId == user.Id && t.TokenHash == hash)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(ct)
            ?? throw new UnauthorizedAccessException("Invalid verification code.");

        if (otpRecord.UsedAt is not null)
            throw new UnauthorizedAccessException("Verification code has already been used.");

        if (otpRecord.ExpiresAt < DateTime.UtcNow)
            throw new UnauthorizedAccessException("Verification code has expired.");

        otpRecord.UsedAt = DateTime.UtcNow;

        // Issue a longer-lived reset token for the password form — same table,
        // different lifetime than the 6-digit OTP row we just consumed.
        var rawToken = GenerateToken();
        _db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = HashToken(rawToken),
            ExpiresAt = DateTime.UtcNow.AddMinutes(_emailSettings.PasswordResetTokenMinutesValid),
        });

        await _db.SaveChangesAsync(ct);
        return rawToken;
    }

    public async Task RequestPasswordResetAsync(string email, string? ipAddress, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email.ToLowerInvariant(), ct);

        // Always succeed silently so callers cannot enumerate registered emails.
        if (user is null) return;

        var existing = await _db.PasswordResetTokens
            .Where(t => t.UserId == user.Id && t.UsedAt == null)
            .ToListAsync(ct);
        foreach (var t in existing) t.UsedAt = DateTime.UtcNow;

        var rawToken = GenerateToken();
        _db.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = HashToken(rawToken),
            IpAddress = ipAddress,
            ExpiresAt = DateTime.UtcNow.AddMinutes(_emailSettings.PasswordResetTokenMinutesValid),
        });

        await _db.SaveChangesAsync(ct);

        var link = $"{_emailSettings.AppBaseUrl.TrimEnd('/')}/reset-password?token={Uri.EscapeDataString(rawToken)}";
        var body = $"<p>Hello {System.Net.WebUtility.HtmlEncode(user.FullName)},</p>" +
                   $"<p>We received a request to reset your Taqreerk password. The link below expires in {_emailSettings.PasswordResetTokenMinutesValid} minutes.</p>" +
                   $"<p><a href=\"{link}\">{link}</a></p>" +
                   "<p>If you did not request a password reset, you can safely ignore this email.</p>";

        await _email.SendEmailAsync(user.Email, "Reset your Taqreerk password", body, ct);
    }

    public async Task ResetPasswordAsync(string token, string newPassword, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Token is required.");
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
            throw new ArgumentException("Password must be at least 8 characters.");

        var hash = HashToken(token);
        var record = await _db.PasswordResetTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct)
            ?? throw new UnauthorizedAccessException("Invalid reset token.");

        if (record.UsedAt is not null)
            throw new UnauthorizedAccessException("Reset token has already been used.");

        if (record.ExpiresAt < DateTime.UtcNow)
            throw new UnauthorizedAccessException("Reset token has expired.");

        record.UsedAt = DateTime.UtcNow;
        record.User.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);

        // Revoke every active refresh token so existing sessions must re-authenticate.
        var sessions = await _db.RefreshTokens
            .Where(t => t.UserId == record.UserId && !t.IsRevoked)
            .ToListAsync(ct);
        foreach (var s in sessions) s.IsRevoked = true;

        await _db.SaveChangesAsync(ct);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Date-only fields from JSON deserialize as Kind=Unspecified; Npgsql timestamptz requires UTC.
    /// </summary>
    private static DateTime? AsUtcDate(DateTime? value)
    {
        if (value is null) return null;
        var dt = value.Value;
        return dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt.Date, DateTimeKind.Utc),
        };
    }

    private static string GenerateToken()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(48);
        // URL-safe base64 so the raw value drops into email links without encoding churn.
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

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
            RefreshToken: rawRefreshToken,
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
