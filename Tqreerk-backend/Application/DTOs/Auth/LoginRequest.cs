using System.ComponentModel.DataAnnotations;

namespace Taqreerk.Application.DTOs.Auth;

public record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required, MinLength(8)] string Password
);
