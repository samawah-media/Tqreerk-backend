using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.DTOs.Reports;

namespace Taqreerk.Application.Interfaces;

/// Admin-side user management. Distinct from IUserService (the user-facing
/// "manage my own profile" surface). Every method here is allowed to see
/// every user regardless of org or role.
public interface IAdminUsersService
{
    Task<PagedResult<AdminUserListItemDto>> ListAsync(
        AdminUsersListRequest req, CancellationToken ct = default);

    Task<AdminUserDetailDto> GetAsync(Guid userId, CancellationToken ct = default);

    /// Set Status = Suspended. Reason required (≥ 5 chars), recorded on the
    /// audit row. Refuses to ban yourself or another platform staff member —
    /// staff are managed via /api/admin/staff which has its own flow.
    Task<AdminUserDetailDto> BanAsync(
        Guid actingUserId, Guid targetUserId, BanUserRequest req, CancellationToken ct = default);

    /// Flip Suspended → Active. Idempotent (writes audit even if the user
    /// wasn't suspended) so the action stays traceable either way.
    Task<AdminUserDetailDto> UnbanAsync(
        Guid actingUserId, Guid targetUserId, CancellationToken ct = default);

    /// Soft-delete the user (DeletedAt). Refuses to delete yourself, refuses
    /// to delete a platform staff member (use /api/admin/staff for that), and
    /// refuses if the user is the sole owner of any organization (the org
    /// would lose its owner-of-record).
    Task DeleteAsync(Guid actingUserId, Guid targetUserId, CancellationToken ct = default);

    /// Reports the user has uploaded across all their org memberships.
    Task<PagedResult<AdminUserReportItemDto>> ListReportsAsync(
        Guid userId, int page, int pageSize, CancellationToken ct = default);
}
