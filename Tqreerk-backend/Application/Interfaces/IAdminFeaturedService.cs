using Taqreerk.Application.DTOs.Admin;

namespace Taqreerk.Application.Interfaces;

/// Editorial curation of the Featured slots. The kanban surface is small
/// — at most a few dozen rows across all sections — so listing is
/// unpaginated. Every write produces an audit row.
public interface IAdminFeaturedService
{
    /// All featured rows across every section, ordered by (Section,
    /// Position). The kanban groups them client-side.
    Task<IReadOnlyList<FeaturedReportDto>> ListAsync(CancellationToken ct = default);

    /// Pin a report to a section. Refuses if the report is already in that
    /// section, if it isn't published, or if Section is unknown.
    Task<FeaturedReportDto> CreateAsync(
        Guid actingUserId, CreateFeaturedReportRequest req, CancellationToken ct = default);

    /// Edit window / activation / section. A section change re-tails the
    /// row in the new column.
    Task<FeaturedReportDto> UpdateAsync(
        Guid actingUserId, Guid id, UpdateFeaturedReportRequest req, CancellationToken ct = default);

    /// Remove a featured-row outright. Cards are editorial decisions and
    /// don't need a soft-delete trail.
    Task DeleteAsync(Guid actingUserId, Guid id, CancellationToken ct = default);

    /// Bulk reorder within a section. Same all-or-nothing shape as the
    /// categories reorder.
    Task ReorderSectionAsync(
        Guid actingUserId, string section, FeaturedReorderRequest req, CancellationToken ct = default);
}
