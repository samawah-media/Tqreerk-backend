using Taqreerk.Application.DTOs.Me;

namespace Taqreerk.Application.Interfaces;

/// Caller-scoped reads for the individual dashboard. The service is the
/// glue between the public report library and the dashboard widgets —
/// it does not own write semantics; saves are mutated via
/// IReportInteractionsService and activity rows are written by
/// IUsageService.
public interface IMeService
{
    Task<IReadOnlyList<MySavedReportDto>> ListSavedReportsAsync(
        Guid userId, int take = 20, CancellationToken ct = default);

    Task<IReadOnlyList<MyActivityItemDto>> ListActivityAsync(
        Guid userId, int take = 10, CancellationToken ct = default);
}
