using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

public class UserInterest : BaseEntity
{
    public Guid UserId { get; set; }
    public Guid? SectorId { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid? CountryId { get; set; }

    public User User { get; set; } = null!;
    public Sector? Sector { get; set; }
    public Organization? Organization { get; set; }
    public Country? Country { get; set; }
}
