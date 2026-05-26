using Taqreerk.Domain.Common;
using Taqreerk.Domain.Enums;

namespace Taqreerk.Domain.Entities;

public class Organization : SoftDeletableEntity
{
    public string NameAr { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public OrganizationType Type { get; set; }
    public string? SectorScope { get; set; }
    public Guid? CountryId { get; set; }
    public string? City { get; set; }
    public string? Phone { get; set; }
    public string? WebsiteUrl { get; set; }
    public string? LogoUrl { get; set; }
    public string? Description { get; set; }
    public bool IsPartner { get; set; }
    public bool IsVerified { get; set; }
    /// Per-org gate for the manual translation feature. When false, the user-side
    /// "Translate" button is hidden and POST /api/reports/{id}/ai/translate is
    /// rejected with 403. Toggled from the admin app's Organizations page by
    /// SuperAdmin / Admin staff. Defaults to false so new orgs can't run AI
    /// translations until staff explicitly opts them in.
    public bool TranslationEnabled { get; set; }
    public OrganizationStatus Status { get; set; } = OrganizationStatus.PendingReview;
    /// User who originally registered the organization. Protected from removal
    /// by other members so the org always has an "owner of record".
    public Guid? CreatedByUserId { get; set; }

    public Country? Country { get; set; }
    public OrganizationProfile? Profile { get; set; }
    public ICollection<OrganizationMember> Members { get; set; } = [];
    public ICollection<Report> Reports { get; set; } = [];
    public ICollection<OrganizationFile> Files { get; set; } = [];
    public ICollection<AiJob> AiJobs { get; set; } = [];
    public ICollection<AuditLog> AuditLogs { get; set; } = [];
    public ICollection<Subscription> Subscriptions { get; set; } = [];
}
