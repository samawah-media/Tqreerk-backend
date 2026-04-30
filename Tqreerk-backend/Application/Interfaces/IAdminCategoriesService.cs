using Taqreerk.Application.DTOs.Admin;

namespace Taqreerk.Application.Interfaces;

/// Admin-side CRUD for sectors and countries. Both tables are small
/// (~50 sectors, ~200 countries at the high end) so listing is unpaginated
/// — the frontend renders everything in a sortable table.
public interface IAdminCategoriesService
{
    // ── Sectors ──────────────────────────────────────────────────────────────

    Task<IReadOnlyList<AdminSectorDto>> ListSectorsAsync(CancellationToken ct = default);

    Task<AdminSectorDto> CreateSectorAsync(
        Guid actingUserId, CreateSectorRequest req, CancellationToken ct = default);

    Task<AdminSectorDto> UpdateSectorAsync(
        Guid actingUserId, Guid id, UpdateSectorRequest req, CancellationToken ct = default);

    /// Refuses if any report or user-interest references this sector.
    /// The error message tells the admin what's blocking the delete.
    Task DeleteSectorAsync(
        Guid actingUserId, Guid id, CancellationToken ct = default);

    /// Bulk re-order in one transaction. The Ids array must contain every
    /// existing sector exactly once — we don't allow partial reorders to
    /// avoid leaving the table in a half-sorted state.
    Task ReorderSectorsAsync(
        Guid actingUserId, ReorderRequest req, CancellationToken ct = default);

    // ── Countries ────────────────────────────────────────────────────────────

    Task<IReadOnlyList<AdminCountryDto>> ListCountriesAsync(CancellationToken ct = default);

    Task<AdminCountryDto> CreateCountryAsync(
        Guid actingUserId, CreateCountryRequest req, CancellationToken ct = default);

    Task<AdminCountryDto> UpdateCountryAsync(
        Guid actingUserId, Guid id, UpdateCountryRequest req, CancellationToken ct = default);

    /// Refuses if any user, organization, or report references this country.
    Task DeleteCountryAsync(
        Guid actingUserId, Guid id, CancellationToken ct = default);

    Task ReorderCountriesAsync(
        Guid actingUserId, ReorderRequest req, CancellationToken ct = default);
}
