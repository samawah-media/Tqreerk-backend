using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.DTOs.Reports;

namespace Taqreerk.Application.Interfaces;

public interface IAdminSubscriptionsService
{
    Task<PagedResult<AdminSubscriptionListItemDto>> ListAsync(
        AdminSubscriptionsListRequest req,
        CancellationToken ct = default);

    Task<AdminSubscriptionDetailDto> GetAsync(Guid subscriptionId, CancellationToken ct = default);

    Task<RefundSubscriptionPaymentResultDto> RefundPaymentAsync(
        Guid actingAdminUserId,
        Guid paymentId,
        RefundSubscriptionPaymentRequest req,
        string? ipAddress,
        CancellationToken ct = default);
}
