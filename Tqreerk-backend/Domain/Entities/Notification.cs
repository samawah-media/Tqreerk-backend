using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

public class Notification : BaseEntity
{
    public Guid UserId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string TitleAr { get; set; } = string.Empty;
    public string TitleEn { get; set; } = string.Empty;
    public string? MessageAr { get; set; }
    public string? MessageEn { get; set; }

    /// <summary>jsonb — arbitrary notification payload</summary>
    public string? Metadata { get; set; }

    public bool IsRead { get; set; }

    public User User { get; set; } = null!;
}
