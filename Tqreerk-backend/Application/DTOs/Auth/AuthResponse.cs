namespace Taqreerk.Application.DTOs.Auth;

public record AuthResponse(
    string AccessToken,
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
