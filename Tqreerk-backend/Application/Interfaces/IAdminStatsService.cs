using Taqreerk.Application.DTOs.Admin;

namespace Taqreerk.Application.Interfaces;

/// One-shot platform stats rollup behind /api/admin/stats/overview.
/// Cheap-by-design: every counter is a single indexed COUNT/SUM and the
/// top-N lists are GROUP BY ... ORDER BY ... LIMIT 10. Heavier rollups
/// (DAU/WAU, search analytics, AI cost) are deferred until their upstream
/// signals land — see AdminStatsDtos.cs for the explicit gaps.
public interface IAdminStatsService
{
    Task<AdminStatsOverviewDto> GetOverviewAsync(CancellationToken ct = default);
}
