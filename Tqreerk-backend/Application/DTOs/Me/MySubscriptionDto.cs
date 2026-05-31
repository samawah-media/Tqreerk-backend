namespace Taqreerk.Application.DTOs.Me;

/// Caller-facing subscription snapshot for settings / checkout / banners.
/// When no row exists, the endpoint returns 204 No Content.
public sealed record MySubscriptionDto(
    Guid SubscriptionId,
    Guid PlanId,
    string PlanNameAr,
    string PlanNameEn,
    string TargetType,
    string Status,
    string PaymentStatus,
    bool IsActive,
    bool AwaitingPayment,
    bool IsOrganizationSubscription,
    Guid? OrganizationId,
    DateTime StartDate,
    DateTime EndDate,
    bool AutoRenew,
    bool HasPaymentToken,
    /// <summary>Org paid period ended — user must renew via checkout.</summary>
    bool RequiresRenewal);
