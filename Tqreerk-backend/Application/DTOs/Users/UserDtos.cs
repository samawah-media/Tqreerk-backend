namespace Taqreerk.Application.DTOs.Users;

public record UserProfileDto(
    Guid Id,
    string Email,
    string? Phone,
    string FullName,
    string UserType,
    string? JobTitle,
    string? InterestField,
    Guid? CountryId,
    bool EmailVerified,
    bool PhoneVerified,
    string PreferredLanguage,
    DateTime CreatedAt
);

public record UpdateProfileRequest(
    string FullName,
    string? JobTitle,
    string? InterestField,
    Guid? CountryId,
    string? PreferredLanguage,
    string? Phone
);

public record UserInterestsDto(
    IReadOnlyList<Guid> SectorIds,
    IReadOnlyList<Guid> OrganizationIds,
    IReadOnlyList<Guid> CountryIds
);

public record SetInterestsRequest(
    IReadOnlyList<Guid>? SectorIds,
    IReadOnlyList<Guid>? OrganizationIds,
    IReadOnlyList<Guid>? CountryIds
);
