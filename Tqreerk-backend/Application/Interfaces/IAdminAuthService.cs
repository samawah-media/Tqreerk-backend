using Taqreerk.Application.DTOs.Admin;

namespace Taqreerk.Application.Interfaces;

public interface IAdminAuthService
{
    /// Resolve the admin profile + permissions for the calling user. Throws
    /// UnauthorizedAccessException if the user is not flagged as platform
    /// staff — the controller maps that to a 403 (handled by the global
    /// exception middleware as 401, see TODO below).
    Task<AdminProfileDto> GetMyProfileAsync(Guid userId, CancellationToken ct = default);
}
