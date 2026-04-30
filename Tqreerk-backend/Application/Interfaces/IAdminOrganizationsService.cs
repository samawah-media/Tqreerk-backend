using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.DTOs.Reports;

namespace Taqreerk.Application.Interfaces;

/// Admin-side organization management. Distinct from IOrganizationService
/// (which is for an org admin managing their own org) so the queries don't
/// have to second-guess scope — every method here is allowed to see every
/// org regardless of membership.
public interface IAdminOrganizationsService
{
    Task<PagedResult<AdminOrganizationListItemDto>> ListAsync(
        AdminOrganizationsListRequest req, CancellationToken ct = default);

    Task<AdminOrganizationDetailDto> GetAsync(Guid orgId, CancellationToken ct = default);

    Task<AdminOrganizationDetailDto> UpdateAsync(
        Guid actingUserId, Guid orgId, UpdateAdminOrganizationRequest req, CancellationToken ct = default);

    /// Flips the IsVerified flag. Idempotent — verifying an already-verified
    /// org is a no-op (still writes an audit row so the action is traceable).
    Task<AdminOrganizationDetailDto> SetVerifiedAsync(
        Guid actingUserId, Guid orgId, bool verified, CancellationToken ct = default);

    /// Sets Status = Suspended. The reason is required and gets stored on
    /// the audit row so we can show it in the org's history later.
    Task<AdminOrganizationDetailDto> SuspendAsync(
        Guid actingUserId, Guid orgId, SuspendOrganizationRequest req, CancellationToken ct = default);

    /// Sets Status = Active. No-op if the org isn't currently suspended,
    /// but still writes an audit row.
    Task<AdminOrganizationDetailDto> ReactivateAsync(
        Guid actingUserId, Guid orgId, CancellationToken ct = default);

    /// Soft-delete (DeletedAt). Refuses if the org has published reports
    /// — those need to be archived first or the public library breaks.
    Task DeleteAsync(Guid actingUserId, Guid orgId, CancellationToken ct = default);

    Task<PagedResult<AdminOrganizationReportItemDto>> ListReportsAsync(
        Guid orgId, int page, int pageSize, CancellationToken ct = default);

    Task<IReadOnlyList<AdminOrganizationMemberDto>> ListMembersAsync(
        Guid orgId, CancellationToken ct = default);
}
