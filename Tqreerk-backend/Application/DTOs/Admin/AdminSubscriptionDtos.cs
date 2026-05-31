namespace Taqreerk.Application.DTOs.Admin;

public record AdminSubscriptionsListRequest(
    string? Q = null,
    string? Status = null,
    string? PaymentStatus = null,
    string? SubscriberType = null,
    int Page = 1,
    int PageSize = 20);

public record AdminSubscriptionListItemDto(
    Guid Id,
    string SubscriberType,
    string SubscriberLabel,
    string? SubscriberEmail,
    Guid PlanId,
    string PlanNameAr,
    string PlanNameEn,
    string Status,
    string PaymentStatus,
    DateTime StartDate,
    DateTime EndDate,
    bool AutoRenew,
    decimal? LastPaidAmount,
    DateTime? LastPaidAt,
    string? LastMoyasarPaymentId,
    Guid? LastPaidPaymentId,
    DateTime CreatedAt);

public record AdminSubscriptionDetailDto(
    Guid Id,
    string SubscriberType,
    string SubscriberLabel,
    string? SubscriberEmail,
    Guid? UserId,
    Guid? OrganizationId,
    Guid PlanId,
    string PlanNameAr,
    string PlanNameEn,
    string Status,
    string PaymentStatus,
    DateTime StartDate,
    DateTime EndDate,
    bool AutoRenew,
    bool HasPaymentToken,
    IReadOnlyList<AdminSubscriptionPaymentDto> Payments);

public record AdminSubscriptionPaymentDto(
    Guid Id,
    decimal Amount,
    string Currency,
    string Status,
    string? PaymentMethod,
    DateTime? PaidAt,
    string? MoyasarPaymentId,
    bool CanRefund,
    DateTime CreatedAt);

public record RefundSubscriptionPaymentRequest(
    string? Reason = null,
    /// Optional partial refund in halalas (100 halalas = 1 SAR). Omit for full refund.
    int? AmountHalalas = null);

public record RefundSubscriptionPaymentResultDto(
    Guid PaymentId,
    Guid SubscriptionId,
    string MoyasarPaymentId,
    string MoyasarStatus,
    decimal RefundedAmountSar,
    bool IsFullRefund,
    string SubscriptionStatus,
    string SubscriptionPaymentStatus);
