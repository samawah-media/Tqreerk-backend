using System.ComponentModel.DataAnnotations;

namespace Taqreerk.Application.DTOs.Admin;

public record AdminPartnerDto(
    Guid Id,
    string NameAr,
    string NameEn,
    string? LogoUrl,
    string? WebsiteUrl,
    bool IsActive,
    int SortOrder
);

public record CreatePartnerRequest(
    [Required, StringLength(200)] string NameAr,
    [Required, StringLength(200)] string NameEn,
    [StringLength(2000)] string? WebsiteUrl,
    bool? IsActive
);

public record UpdatePartnerRequest(
    [StringLength(200)] string? NameAr,
    [StringLength(200)] string? NameEn,
    [StringLength(2000)] string? WebsiteUrl,
    bool? IsActive
);
