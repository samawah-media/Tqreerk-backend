using System.ComponentModel.DataAnnotations;

namespace Taqreerk.Application.DTOs.Admin;

public record AdminPartnerCategoryDto(
    Guid Id,
    string NameAr,
    string NameEn,
    bool IsActive,
    int SortOrder,
    int PartnerCount
);

public record CreatePartnerCategoryRequest(
    [Required, StringLength(200)] string NameAr,
    [Required, StringLength(200)] string NameEn,
    bool? IsActive
);

public record UpdatePartnerCategoryRequest(
    [StringLength(200)] string? NameAr,
    [StringLength(200)] string? NameEn,
    bool? IsActive
);

public record AdminPartnerDto(
    Guid Id,
    Guid CategoryId,
    string CategoryNameAr,
    string CategoryNameEn,
    string NameAr,
    string NameEn,
    string? LogoUrl,
    string? WebsiteUrl,
    bool IsActive,
    int SortOrder
);

public record CreatePartnerRequest(
    [Required] Guid CategoryId,
    [Required, StringLength(200)] string NameAr,
    [Required, StringLength(200)] string NameEn,
    [StringLength(2000)] string? WebsiteUrl,
    bool? IsActive
);

public record UpdatePartnerRequest(
    Guid? CategoryId,
    [StringLength(200)] string? NameAr,
    [StringLength(200)] string? NameEn,
    [StringLength(2000)] string? WebsiteUrl,
    bool? IsActive
);

public record PartnerReorderRequest(
    [Required] Guid CategoryId,
    [Required, MinLength(1)] IReadOnlyList<Guid> Ids
);
