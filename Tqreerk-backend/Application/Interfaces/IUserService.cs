using Taqreerk.Application.DTOs.Users;

namespace Taqreerk.Application.Interfaces;

public interface IUserService
{
    Task<UserProfileDto> GetProfileAsync(Guid userId, CancellationToken ct = default);
    Task<UserProfileDto> UpdateProfileAsync(Guid userId, UpdateProfileRequest req, CancellationToken ct = default);

    Task<UserInterestsDto> GetInterestsAsync(Guid userId, CancellationToken ct = default);
    Task<UserInterestsDto> SetInterestsAsync(Guid userId, SetInterestsRequest req, CancellationToken ct = default);

    /// Verify the user's current password and replace it with the new one.
    /// Throws UnauthorizedAccessException when the current password is
    /// wrong, ArgumentException when the new password is invalid.
    Task ChangePasswordAsync(Guid userId, ChangePasswordRequest req, CancellationToken ct = default);
}
