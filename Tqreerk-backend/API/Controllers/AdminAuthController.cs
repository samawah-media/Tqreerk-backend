using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.DTOs.Auth;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

/// Admin SPA authentication helpers. Login + refresh + logout still go
/// through the shared /api/auth/* endpoints — this controller only holds
/// admin-specific identity calls. Most importantly it owns the
/// "is this caller actually staff?" check the admin SPA hits right after
/// login to decide whether to render the dashboard or kick the user back
/// to the login page with an error.
///
/// Also owns the 2FA lifecycle for staff: setup → activate → verify (the
/// step-2 login exchange) plus regenerate-backup-codes and a status read.
[ApiController]
[Route("api/admin/auth")]
[Produces("application/json")]
[Authorize]
public class AdminAuthController : ControllerBase
{
    private const string RefreshTokenCookie = "refresh_token";
    private readonly IAdminAuthService _adminAuth;
    private readonly ITwoFactorService _twoFactor;
    private readonly IAuthService _auth;
    private readonly IWebHostEnvironment _env;

    public AdminAuthController(
        IAdminAuthService adminAuth,
        ITwoFactorService twoFactor,
        IAuthService auth,
        IWebHostEnvironment env)
    {
        _adminAuth = adminAuth;
        _twoFactor = twoFactor;
        _auth = auth;
        _env = env;
    }

    /// <summary>Return the calling user's admin profile. 403 if the user is
    /// not flagged as platform staff — the SPA uses this to refuse access
    /// to non-staff who happen to have a valid JWT from the user app.</summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(AdminProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _adminAuth.GetMyProfileAsync(userId, ct));
    }

    /// <summary>Begin 2FA setup. Generates a fresh TOTP secret + backup codes
    /// and returns them once. The user must call /activate with a valid TOTP
    /// code before 2FA actually takes effect.</summary>
    [HttpPost("2fa/setup")]
    [ProducesResponseType(typeof(TwoFactorSetupResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> StartTwoFactorSetup(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await _twoFactor.StartSetupAsync(userId, ct);
        return Ok(result);
    }

    /// <summary>Activate 2FA with a TOTP code from the user's authenticator,
    /// proving they successfully scanned the QR. Required after /setup before
    /// the next login will challenge for 2FA.</summary>
    [HttpPost("2fa/activate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ActivateTwoFactor(
        [FromBody] TwoFactorCodeRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        await _twoFactor.ActivateAsync(userId, req.Code, ct);
        return NoContent();
    }

    /// <summary>Step 2 of staff login. Exchanges the challenge token from
    /// /api/auth/login + a TOTP (or backup) code for a real access/refresh
    /// pair. Anonymous: the challenge token itself authenticates the call.</summary>
    [HttpPost("2fa/verify")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> VerifyTwoFactor(
        [FromBody] TwoFactorVerifyRequest req, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var device = Request.Headers.UserAgent.ToString();

        var result = await _auth.CompleteTwoFactorLoginAsync(
            req.ChallengeToken, req.Code, ip, device, ct);

        AppendRefreshCookie(result.RefreshToken);
        return Ok(result.Response);
    }

    /// <summary>Mint a fresh batch of backup codes, invalidating the old
    /// set. Requires 2FA to already be active.</summary>
    [HttpPost("2fa/regenerate-backup-codes")]
    [ProducesResponseType(typeof(TwoFactorBackupCodesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RegenerateBackupCodes(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await _twoFactor.RegenerateBackupCodesAsync(userId, ct);
        return Ok(result);
    }

    /// <summary>Status of the current user's 2FA configuration. Used by the
    /// admin SPA's settings page to render the right buttons (setup vs.
    /// regenerate vs. nothing).</summary>
    [HttpGet("2fa/status")]
    [ProducesResponseType(typeof(TwoFactorStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetTwoFactorStatus(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await _twoFactor.GetStatusAsync(userId, ct);
        return Ok(result);
    }

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// Mirror of AuthController.AppendRefreshCookie — same Path, same flags,
    /// same dev-vs-prod branching. Duplicated rather than extracted because
    /// the two controllers don't share a base; if we grow a third caller this
    /// becomes a CookieHelper service.
    private void AppendRefreshCookie(string refreshToken)
    {
        var isDev = _env.IsDevelopment();

        Response.Cookies.Append(RefreshTokenCookie, refreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = !isDev,
            SameSite = isDev ? SameSiteMode.Lax : SameSiteMode.Strict,
            Expires = DateTimeOffset.UtcNow.AddDays(7),
            Path = "/api/auth",
        });
    }
}
