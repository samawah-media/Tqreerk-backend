using Taqreerk.Application.DTOs.Dashboard;

namespace Taqreerk.Application.Interfaces;

public interface IDashboardService
{
    Task<OrganizationStatsDto> GetOrganizationStatsAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<RecentActivityDto>> GetRecentActivityAsync(Guid userId, int take = 10, CancellationToken ct = default);
}
