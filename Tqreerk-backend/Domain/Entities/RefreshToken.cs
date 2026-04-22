using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

public class RefreshToken : BaseEntity
{
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string? DeviceInfo { get; set; }
    public string? IpAddress { get; set; }
    public bool IsRevoked { get; set; }
    public DateTime ExpiresAt { get; set; }

    public User User { get; set; } = null!;
}
