using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.Application.DTOs.Auth;
using Taqreerk.Application.DTOs.Rbac;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

[ApiController]
[Route("api/auth")]
[Produces("application/json")]
public class AuthController : ControllerBase
{
    private const string RefreshTokenCookie = "refresh_token";
    private readonly IAuthService _auth;
    private readonly IRbacService _rbac;
    private readonly IWebHostEnvironment _env;

    public AuthController(IAuthService auth, IRbacService rbac, IWebHostEnvironment env)
    {
        _auth = auth;
        _rbac = rbac;
        _env = env;
    }

    /// <summary>Register a new individual user account.</summary>
    [HttpPost("register/individual")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RegisterIndividual([FromBody] RegisterIndividualRequest req, CancellationToken ct)
    {
        var result = await _auth.RegisterIndividualAsync(req, ct);
        AppendRefreshCookie(result.RefreshToken);
        return Ok(result.Response);
    }

    /// <summary>Register a new organization account.</summary>
    [HttpPost("register/organization")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RegisterOrganization([FromBody] RegisterOrganizationRequest req, CancellationToken ct)
    {
        var result = await _auth.RegisterOrganizationAsync(req, ct);
        AppendRefreshCookie(result.RefreshToken);
        return Ok(result.Response);
    }

    /// <summary>Authenticate with email and password. Returns either a
    /// full token pair (sets refresh token as HttpOnly cookie) OR a 2FA
    /// challenge when the caller is platform staff with 2FA enabled.
    /// The SPA inspects the response shape and branches accordingly.</summary>
    [HttpPost("login")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var device = Request.Headers.UserAgent.ToString();

        var outcome = await _auth.LoginWithTwoFactorAsync(req, ip, device, ct);

        if (outcome.Tokens is { } tokens)
        {
            AppendRefreshCookie(tokens.RefreshToken);
            return Ok(new LoginResponse(Tokens: tokens.Response, TwoFactor: null));
        }

        // 2FA challenge path. Don't set the refresh cookie here — the
        // user hasn't proven possession of the second factor yet. The
        // /admin/auth/2fa/verify endpoint sets it after step 2 succeeds.
        var challenge = outcome.TwoFactorChallenge!;
        return Ok(new LoginResponse(
            Tokens: null,
            TwoFactor: new TwoFactorChallenge(
                ChallengeToken: challenge.ChallengeToken,
                Email: challenge.Email,
                IsConfigured: true)));
    }

    /// <summary>Exchange a refresh token (from body or cookie) for a new token pair.</summary>
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequest? body, CancellationToken ct)
    {
        // Prefer the body over the cookie. The cookie can drift out of sync
        // with the SPA's localStorage copy through the dev proxy (a cancelled
        // refresh response can leave the browser with a rotated cookie value
        // that the SPA never saw). Both ends agree on the body, so make that
        // the source of truth and let the cookie act as a fallback for older
        // clients that don't echo the token in the body.
        var token = !string.IsNullOrWhiteSpace(body?.RefreshToken)
            ? body!.RefreshToken
            : Request.Cookies[RefreshTokenCookie];

        if (string.IsNullOrWhiteSpace(token))
            return Unauthorized(new { title = "Refresh token is required." });

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var result = await _auth.RefreshAsync(token, ip, ct);
        AppendRefreshCookie(result.RefreshToken);
        return Ok(result.Response);
    }

    /// <summary>Revoke the current refresh token and clear the cookie.</summary>
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var token = Request.Cookies[RefreshTokenCookie];
        if (!string.IsNullOrWhiteSpace(token))
            await _auth.RevokeTokenAsync(token, ct);

        Response.Cookies.Delete(RefreshTokenCookie);
        return NoContent();
    }

    /// <summary>Send (or resend) an email verification link to the given address.</summary>
    [HttpPost("verify-email/send")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> SendVerificationEmail([FromBody] SendVerificationEmailRequest req, CancellationToken ct)
    {
        await _auth.SendVerificationEmailAsync(req.Email, ct);
        return Accepted();
    }

    /// <summary>Send a 6-digit verification code to the given email. Used by the OTP verify screen.</summary>
    [HttpPost("otp/email/send")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SendEmailOtp([FromBody] SendEmailOtpRequest req, CancellationToken ct)
    {
        await _auth.SendEmailOtpAsync(req.Email, ct);
        return Accepted();
    }

    /// <summary>Resend the email OTP. Subject to a per-user cooldown.</summary>
    [HttpPost("otp/email/resend")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<IActionResult> ResendEmailOtp([FromBody] SendEmailOtpRequest req, CancellationToken ct)
        => SendEmailOtp(req, ct);

    /// <summary>Verify a 6-digit email OTP. On success, marks the email verified and issues a fresh token pair.</summary>
    [HttpPost("otp/email/verify")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> VerifyEmailOtp([FromBody] VerifyEmailOtpRequest req, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var device = Request.Headers.UserAgent.ToString();

        var result = await _auth.VerifyEmailOtpAsync(req.Email, req.Code, ip, device, ct);
        AppendRefreshCookie(result.RefreshToken);
        return Ok(result.Response);
    }

    /// <summary>Confirm an email address using the token from the verification email.</summary>
    [HttpPost("verify-email/confirm")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ConfirmEmail([FromBody] ConfirmEmailRequest req, CancellationToken ct)
    {
        await _auth.ConfirmEmailAsync(req.Token, ct);
        return NoContent();
    }

    /// <summary>Initiate a password reset. Always returns 202 — existence of the email is never revealed.</summary>
    [HttpPost("forgot-password")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest req, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        await _auth.RequestPasswordResetAsync(req.Email, ip, ct);
        return Accepted();
    }

    /// <summary>Complete a password reset using the token from the email. Revokes all existing sessions.</summary>
    [HttpPost("reset-password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest req, CancellationToken ct)
    {
        await _auth.ResetPasswordAsync(req.Token, req.NewPassword, ct);
        return NoContent();
    }

    /// <summary>Returns pages, roles, and granted permission keys for the current user.</summary>
    [HttpGet("me/permissions")]
    [Authorize]
    [ProducesResponseType(typeof(MePermissionsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyPermissions(CancellationToken ct)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(sub, out var userId))
            return Unauthorized();

        var result = await _rbac.GetEffectivePermissionsAsync(userId, ct);
        return Ok(result);
    }

    /// <summary>List the current user's active sessions (one per device). The current device is flagged.</summary>
    [HttpGet("sessions")]
    [Authorize]
    [ProducesResponseType(typeof(IReadOnlyList<SessionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSessions(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();

        var current = Request.Cookies[RefreshTokenCookie];
        var sessions = await _auth.GetActiveSessionsAsync(userId, current, ct);
        return Ok(sessions);
    }

    /// <summary>Revoke a specific session (other than the current). The owning user must match.</summary>
    [HttpDelete("sessions/{sessionId:guid}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeSession(Guid sessionId, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();

        await _auth.RevokeSessionAsync(userId, sessionId, ct);
        return NoContent();
    }

    /// <summary>Revoke every active session for the current user and clear the refresh cookie.</summary>
    [HttpPost("logout-all")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> LogoutAll(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();

        await _auth.RevokeAllSessionsAsync(userId, ct);
        Response.Cookies.Delete(RefreshTokenCookie);
        return NoContent();
    }

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private void AppendRefreshCookie(string refreshToken)
    {
        // In Development we are served over plain HTTP via the Vite proxy, so the cookie
        // must be non-Secure and Lax (same-site through the proxy).
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
