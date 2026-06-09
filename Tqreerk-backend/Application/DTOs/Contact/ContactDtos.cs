using System.ComponentModel.DataAnnotations;

namespace Taqreerk.Application.DTOs.Contact;

public record SubmitContactRequest(
    [Required, MaxLength(120)] string FullName,
    [Required, EmailAddress, MaxLength(255)] string Email,
    [MaxLength(30)] string? Phone,
    [Required, RegularExpression("^(suggestion|complaint|inquiry|partner)$")] string Type,
    [MaxLength(2000)] string? Message,
    [MaxLength(200)] string? Organization = null
);

public record SubmitContactResponse(
    string Message
);
