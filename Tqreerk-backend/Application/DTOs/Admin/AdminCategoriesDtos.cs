using System.ComponentModel.DataAnnotations;

namespace Taqreerk.Application.DTOs.Admin;

/// Sectors are returned with their reference count so the admin page can
/// disable the delete button when the row is in use, without having to
/// hit a separate "is-deletable" endpoint per row.
public record AdminSectorDto(
    Guid Id,
    string NameAr,
    string NameEn,
    string Slug,
    string? Description,
    bool IsActive,
    int SortOrder,
    /// Reports + UserInterests referencing this sector. > 0 means delete
    /// will be refused at the API. Computed on every list call — cheap
    /// (indexed COUNT) given the small row count.
    int ReferenceCount
);

public record CreateSectorRequest(
    [Required, StringLength(150)] string NameAr,
    [Required, StringLength(150)] string NameEn,
    [Required, StringLength(150)] string Slug,
    [StringLength(2000)] string? Description,
    bool? IsActive
);

public record UpdateSectorRequest(
    [StringLength(150)] string? NameAr,
    [StringLength(150)] string? NameEn,
    [StringLength(150)] string? Slug,
    [StringLength(2000)] string? Description,
    bool? IsActive
);

/// Same shape pattern as sectors. ISO code is required and unique. Reference
/// count totals users + organizations + reports referencing the country,
/// since deletion would orphan any of them.
public record AdminCountryDto(
    Guid Id,
    string NameAr,
    string NameEn,
    string IsoCode,
    int SortOrder,
    int ReferenceCount
);

public record CreateCountryRequest(
    [Required, StringLength(150)] string NameAr,
    [Required, StringLength(150)] string NameEn,
    [Required, StringLength(10)] string IsoCode
);

public record UpdateCountryRequest(
    [StringLength(150)] string? NameAr,
    [StringLength(150)] string? NameEn,
    [StringLength(10)] string? IsoCode
);

/// Body of POST /api/admin/sectors/reorder and /api/admin/countries/reorder.
/// The new order is the array order — index 0 becomes SortOrder=0, and so
/// on. The server validates that the array matches the existing rows (no
/// missing or extra IDs) before applying.
public record ReorderRequest(
    [Required, MinLength(1)] IReadOnlyList<Guid> Ids
);
