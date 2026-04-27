namespace Taqreerk.Application.DTOs.Auth;

public record SessionDto(
    Guid Id,
    string? DeviceInfo,
    string? IpAddress,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    bool IsCurrent
);
