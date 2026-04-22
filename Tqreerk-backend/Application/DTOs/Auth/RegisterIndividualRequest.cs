using System.ComponentModel.DataAnnotations;

namespace Taqreerk.Application.DTOs.Auth;

public record RegisterIndividualRequest(
    [Required, MaxLength(200)] string FullName,
    [Required, EmailAddress, MaxLength(255)] string Email,
    [Required, MinLength(8)] string Password,
    [Phone, MaxLength(20)] string? Phone,
    [MaxLength(150)] string? JobTitle,
    [MaxLength(150)] string? InterestField,
    Guid? CountryId,
    [MaxLength(5)] string PreferredLanguage = "ar"
);
