using System.ComponentModel.DataAnnotations;
using Taqreerk.Domain.Enums;

namespace Taqreerk.Application.DTOs.Organizations;

/// Full organization-with-profile snapshot returned by GET /api/organizations/me.
public record OrganizationDetailDto(
    Guid Id,
    string NameAr,
    string NameEn,
    string Slug,
    string Type,
    string Status,
    bool IsVerified,
    bool IsPartner,
    Guid? CountryId,
    string? City,
    string? Phone,
    string? WebsiteUrl,
    string? SectorScope,
    string? LogoUrl,
    string? Description,
    OrganizationProfileDto? Profile,
    OrganizationFileDto? CommercialRegisterFile,
    bool ProfileComplete,
    DateTime CreatedAt
);

public record OrganizationProfileDto(
    string? CommercialRegisterNo,
    string? LicenseDocumentUrl,
    bool IssuesReports,
    int AnnualReportsCount,
    bool WantsToPublish,
    bool InterestedInSubscription,
    string? ContactPersonName,
    string? ContactPersonTitle,
    string? ContactEmail,
    bool PoliciesAccepted,
    DateTime? PoliciesAcceptedAt
);

public record OrganizationFileDto(
    Guid Id,
    string FileType,
    string FileUrl,
    DateTime CreatedAt
);

/// Rename the caller's organization. Both fields are required because the
/// platform always renders org names in both languages; an empty side would
/// leave one locale showing a blank label.
public record UpdateOrganizationNamesRequest(
    [Required, MaxLength(300)] string NameAr,
    [Required, MaxLength(300)] string NameEn
);

/// Step 1: basic info. All fields optional individually so users can save partial progress;
/// completeness is judged by the server before flipping status to Active.
public record UpdateOrganizationBasicsRequest(
    Guid? CountryId,
    [MaxLength(100)] string? City,
    [Phone, MaxLength(20)] string? Phone,
    [MaxLength(100)] string? CommercialRegisterNo
);

/// Step 2: type & scope.
public record UpdateOrganizationScopeRequest(
    OrganizationType? Type,
    [MaxLength(100)] string? SectorScope,
    [MaxLength(500)] string? WebsiteUrl,
    [MaxLength(2000)] string? Description
);

/// Step 3: reports.
public record UpdateOrganizationReportsRequest(
    bool? IssuesReports,
    [Range(0, int.MaxValue)] int? AnnualReportsCount,
    bool? WantsToPublish,
    bool? InterestedInSubscription
);

/// Step 4: contact + policy acceptance.
public record UpdateOrganizationContactRequest(
    [MaxLength(200)] string? ContactPersonName,
    [MaxLength(200)] string? ContactPersonTitle,
    [EmailAddress, MaxLength(255)] string? ContactEmail,
    bool? PoliciesAccepted
);

/// Reference data DTOs for dropdowns.
public record CountryDto(Guid Id, string NameAr, string NameEn, string IsoCode);
public record SectorDto(Guid Id, string NameAr, string NameEn, string Slug);
