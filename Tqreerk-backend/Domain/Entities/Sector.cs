using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

public class Sector : BaseEntity
{
    public string NameAr { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    /// Manual ordering driven by the admin Categories page (drag-and-drop).
    /// Lower value = earlier in lists. Defaults to 0; rebalanced server-side
    /// on /reorder so the values stay dense.
    public int SortOrder { get; set; }

    public ICollection<Report> Reports { get; set; } = [];
    public ICollection<UserInterest> UserInterests { get; set; } = [];
}
