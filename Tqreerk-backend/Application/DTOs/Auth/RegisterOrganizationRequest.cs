using System.ComponentModel.DataAnnotations;
using Taqreerk.Domain.Enums;

namespace Taqreerk.Application.DTOs.Auth;

public record RegisterOrganizationRequest(
    // Organization account credentials
    [Required, EmailAddress, MaxLength(255)] string Email,
    [Required, MinLength(8)] string Password,

    // Organization info
    [Required, MaxLength(300)] string NameAr,
    [Required, MaxLength(300)] string NameEn,
    [Required] OrganizationType Type,
    Guid? CountryId,
    [MaxLength(100)] string? City,
    [Phone, MaxLength(20)] string? Phone,
    [MaxLength(500)] string? WebsiteUrl,
    [MaxLength(100)] string? SectorScope,

    // Profile / onboarding
    [MaxLength(100)] string? CommercialRegisterNo,
    bool IssuesReports = false,
    int AnnualReportsCount = 0,
    bool WantsToPublish = false,
    bool InterestedInSubscription = false,
    [MaxLength(200)] string? ContactPersonName = null,
    [MaxLength(150)] string? ContactPersonTitle = null,
    [EmailAddress, MaxLength(255)] string? ContactEmail = null,
    bool PoliciesAccepted = false
);
