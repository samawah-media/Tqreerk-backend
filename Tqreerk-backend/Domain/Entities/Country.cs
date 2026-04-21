using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

public class Country : BaseEntity
{
    public string NameAr { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string IsoCode { get; set; } = string.Empty;

    public ICollection<User> Users { get; set; } = [];
    public ICollection<Organization> Organizations { get; set; } = [];
    public ICollection<Report> Reports { get; set; } = [];
    public ICollection<UserInterest> UserInterests { get; set; } = [];
}
