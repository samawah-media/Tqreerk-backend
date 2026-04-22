using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

public class Sector : BaseEntity
{
    public string NameAr { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<Report> Reports { get; set; } = [];
    public ICollection<UserInterest> UserInterests { get; set; } = [];
}
