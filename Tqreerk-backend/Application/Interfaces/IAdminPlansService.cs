using Taqreerk.Application.DTOs.Admin;

namespace Taqreerk.Application.Interfaces;

/// Admin curation surface for the plan catalogue. Plans are seeded by
/// SQL but tuned over time by the business — pricing, AI quotas, the
/// odd boolean flag — so this service exists to keep that loop fast
/// without needing a migration per tweak.
///
/// We deliberately don't expose Create / Delete here for v1: the four
/// canonical plans (Free / Annual / Basic / Professional) are
/// load-bearing for the rest of the app (registration auto-link,
/// upgrade flow, downloads percentage rule). Adding a new tier needs
/// thought beyond the table, so it stays a SQL migration for now.
public interface IAdminPlansService
{
    /// Every plan the catalogue knows about, ordered by target type
    /// then price. `IsActive` is included on the row so the admin can
    /// toggle it in-place.
    Task<IReadOnlyList<AdminPlanDto>> ListAsync(CancellationToken ct = default);

    Task<AdminPlanDto> GetAsync(Guid planId, CancellationToken ct = default);

    /// Partial update. Any null field on the request is left alone.
    /// Returns the refreshed DTO so the editor can echo the saved
    /// state back without a follow-up GET.
    Task<AdminPlanDto> UpdateAsync(
        Guid actingAdminUserId,
        Guid planId,
        UpdateAdminPlanRequest req,
        string? ipAddress,
        CancellationToken ct = default);
}
