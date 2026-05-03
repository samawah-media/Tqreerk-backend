using Taqreerk.Domain.Enums;

namespace Taqreerk.Application.DTOs.Points;

public sealed record PointsBalanceDto(
    int Balance,
    int WelcomeBalance,
    DateTime UpdatedAt);

public sealed record PointTransactionDto(
    Guid Id,
    int Amount,
    int BalanceAfter,
    string Reason,
    UsageActionType? ActionType,
    Guid? ResourceId,
    DateTime CreatedAt);

public sealed record PointsHistoryPageDto(
    IReadOnlyList<PointTransactionDto> Items,
    int Page,
    int PageSize,
    int TotalItems);
