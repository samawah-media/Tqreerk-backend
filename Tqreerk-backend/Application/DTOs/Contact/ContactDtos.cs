using System.ComponentModel.DataAnnotations;

namespace Taqreerk.Application.DTOs.Contact;

public record SubmitContactRequest(
    [Required, MaxLength(120)] string FullName,
    [Required, EmailAddress, MaxLength(255)] string Email,
    [MaxLength(30)] string? Phone,
    [Required, RegularExpression("^(suggestion|complaint|inquiry)$")] string Type,
    [Required, MinLength(10), MaxLength(2000)] string Message
);

public record SubmitContactResponse(
    string Message
);
