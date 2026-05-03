using Taqreerk.Domain.Enums;

namespace Taqreerk.Application.DTOs.Usage;

/// One counter for the dashboard widget. `Limit = -1` is unlimited;
/// `Remaining` reflects that as int.MaxValue so the UI can branch on it.
public sealed record UsageCounterDto(
    UsageActionType ActionType,
    int Limit,
    int Used,
    int Remaining,
    bool IsUnlimited,
    bool IsExceeded);

/// What GET /api/usage/me returns. The plan name is rendered above the
/// counters so the user knows which limits apply.
public sealed record UsageSummaryDto(
    Guid SubscriptionId,
    Guid PlanId,
    string PlanNameAr,
    string PlanNameEn,
    DateOnly BillingPeriodStart,
    DateTime ResetsAt,
    IReadOnlyList<UsageCounterDto> Counters);

public sealed record UsageHistoryItemDto(
    Guid Id,
    UsageActionType ActionType,
    Guid? ResourceId,
    DateTime ConsumedAt);

public sealed record UsageHistoryPageDto(
    IReadOnlyList<UsageHistoryItemDto> Items,
    int Page,
    int PageSize,
    int TotalItems);
