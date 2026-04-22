namespace Taqreerk.Application.DTOs.Auth;

/// <summary>
/// Internal result carrying both tokens.
/// The refresh token is set as an HttpOnly cookie by the controller; it never appears in the JSON body.
/// </summary>
public record AuthResult(AuthResponse Response, string RefreshToken);
