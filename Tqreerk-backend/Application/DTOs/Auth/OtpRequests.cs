using System.ComponentModel.DataAnnotations;

namespace Taqreerk.Application.DTOs.Auth;

public record SendEmailOtpRequest(
    [Required, EmailAddress, MaxLength(255)] string Email
);

public record VerifyEmailOtpRequest(
    [Required, EmailAddress, MaxLength(255)] string Email,
    [Required, RegularExpression("^[0-9]{6}$", ErrorMessage = "Code must be 6 digits.")] string Code
);
