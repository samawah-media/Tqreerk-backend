using Taqreerk.Domain.Enums;

namespace Taqreerk.Domain.Entities;

/// Temporary holding row created when a user submits the registration form.
/// Converted into a real User row only after OTP verification succeeds.
/// Rows that were never verified expire after ExpiresAt and can be cleaned up.
public class PendingRegistration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string UserType { get; set; } = "individual";

    // Individual-only fields
    public string? JobTitle { get; set; }
    public string? InterestField { get; set; }
    public Guid? CountryId { get; set; }
    public string PreferredLanguage { get; set; } = "ar";

    // Organization-only fields (null for individual registrations)
    public string? OrgNameAr { get; set; }
    public string? OrgNameEn { get; set; }
    public OrganizationType? OrgType { get; set; }
    public string? OrgCity { get; set; }
    public string? OrgWebsiteUrl { get; set; }
    public string? OrgSectorScope { get; set; }
    public string? OrgCommercialRegisterNo { get; set; }
    public string? OrgCommercialRegisterName { get; set; }
    public DateTime? OrgCommercialRegisterExpiryDate { get; set; }
    public string? OrgTaxNumber { get; set; }
    public string? OrgLicenseDocumentUrl { get; set; }
    public int? OrgEmployeeCount { get; set; }
    public bool OrgIssuesReports { get; set; }
    public int? OrgAnnualReportsCount { get; set; }
    public bool OrgWantsToPublish { get; set; }
    public bool OrgInterestedInSubscription { get; set; }
    public string? OrgContactPersonName { get; set; }
    public string? OrgContactPersonTitle { get; set; }
    public string? OrgContactEmail { get; set; }
    public bool OrgPoliciesAccepted { get; set; }

    public string OtpHash { get; set; } = string.Empty;
    public DateTime OtpExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddHours(1);
}
