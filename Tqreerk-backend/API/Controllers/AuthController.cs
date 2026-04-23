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

    public AuthController(IAuthService auth, IRbacService rbac)
    {
        _auth = auth;
        _rbac = rbac;
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

    /// <summary>Authenticate with email and password. Returns access token; sets refresh token as HttpOnly cookie.</summary>
    [HttpPost("login")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var device = Request.Headers.UserAgent.ToString();

        var result = await _auth.LoginAsync(req, ip, device, ct);
        AppendRefreshCookie(result.RefreshToken);
        return Ok(result.Response);
    }

    /// <summary>Exchange a refresh token (from cookie or body) for a new token pair.</summary>
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequest? body, CancellationToken ct)
    {
        var token = Request.Cookies[RefreshTokenCookie] ?? body?.RefreshToken;

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

    // ── helpers ──────────────────────────────────────────────────────────────

    private void AppendRefreshCookie(string refreshToken) =>
        Response.Cookies.Append(RefreshTokenCookie, refreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Expires = DateTimeOffset.UtcNow.AddDays(7),
            Path = "/api/auth",
        });
}
