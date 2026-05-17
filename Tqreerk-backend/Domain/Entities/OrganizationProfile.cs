using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

public class OrganizationProfile : BaseEntity
{
    public Guid OrganizationId { get; set; }
    public string? CommercialRegisterNo { get; set; }
    public string? CommercialRegisterName { get; set; }
    public DateTime? CommercialRegisterExpiryDate { get; set; }
    public string? TaxNumber { get; set; }
    public int? EmployeeCount { get; set; }
    public string? LicenseDocumentUrl { get; set; }
    public bool IssuesReports { get; set; }
    public int AnnualReportsCount { get; set; }
    public bool WantsToPublish { get; set; }
    public bool InterestedInSubscription { get; set; }
    public string? ContactPersonName { get; set; }
    public string? ContactPersonTitle { get; set; }
    public string? ContactEmail { get; set; }
    public bool PoliciesAccepted { get; set; }
    public DateTime? PoliciesAcceptedAt { get; set; }

    public Organization Organization { get; set; } = null!;
}
