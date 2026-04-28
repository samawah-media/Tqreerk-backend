namespace Taqreerk.Application.DTOs.Auth;

public record AuthResponse(
    string AccessToken,
    string RefreshToken,
    DateTime ExpiresAt,
    UserProfile User
);

public record UserProfile(
    Guid Id,
    string FullName,
    string Email,
    string UserType,
    string PreferredLanguage
);

/// JSON shape returned by POST /api/auth/login. Either `tokens` is set
/// (normal login) OR `twoFactor` is set (staff user with 2FA enabled —
/// SPA must redirect to the verify page and exchange the challenge token).
/// Exactly one of the two is non-null.
public record LoginResponse(
    AuthResponse? Tokens,
    TwoFactorChallenge? TwoFactor
);

public record TwoFactorChallenge(
    string ChallengeToken,
    string Email,
    /// True if the user has completed setup (so they need a real TOTP
    /// code from their authenticator). False if they haven't set up yet —
    /// the SPA routes them through the setup wizard before /verify.
    bool IsConfigured
);
