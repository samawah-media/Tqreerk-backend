using System.ComponentModel.DataAnnotations;

namespace Taqreerk.Application.DTOs.Auth;

public record RefreshTokenRequest(
    [Required] string RefreshToken
);
