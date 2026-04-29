namespace Taqreerk.Application.DTOs.Auth;

/// <summary>
/// Internal result carrying both tokens.
/// The refresh token is set as an HttpOnly cookie by the controller; it
/// never appears in the JSON body.
/// </summary>
public record AuthResult(AuthResponse Response, string RefreshToken);

/// <summary>
/// Login result when the user is platform staff with 2FA enabled. We
/// don't issue real tokens at step 1 — instead the SPA gets a short-lived
/// challenge token to swap for full tokens via /api/admin/auth/2fa/verify.
/// AuthService returns this in place of AuthResult; the controller picks
/// which to serialize based on which property is non-null.
/// </summary>
public record TwoFactorChallengeResult(string ChallengeToken, string Email);

/// <summary>
/// Discriminated wrapper so AuthService.LoginAsync can return either a
/// full token pair (the common case) or a 2FA challenge (staff with 2FA
/// enabled) without bloating its signature with an out parameter or two
/// methods. Exactly one of the two properties is non-null.
/// </summary>
public record LoginOutcome(
    AuthResult? Tokens,
    TwoFactorChallengeResult? TwoFactorChallenge
);
