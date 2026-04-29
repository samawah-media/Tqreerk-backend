using Taqreerk.Application.DTOs.Admin;

namespace Taqreerk.Application.Interfaces;

/// Admin-side dashboard rollups. Distinct from the org-scoped
/// IDashboardService because the queries cross every tenant — keeping
/// them separate avoids accidental scope mixing.
public interface IAdminDashboardService
{
    /// Counts that drive the topbar badges. Cheap to compute (three COUNTs)
    /// because the SPA hits this on a 30s timer.
    Task<AdminQuickStatsDto> GetQuickStatsAsync(CancellationToken ct = default);
}
