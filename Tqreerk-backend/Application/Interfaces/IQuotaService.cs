using Taqreerk.Domain.Enums;

namespace Taqreerk.Application.Interfaces;

/// Per-organization daily quotas for AI operations (ingest / translate /
/// chat). Implementations count rolling 24-hour usage and throw
/// QuotaExceededException when an org has used up its allowance.
public interface IQuotaService
{
    /// Throws QuotaExceededException if `organizationId` has hit its daily
    /// cap for the given AI job type. No-op when quotas are disabled or
    /// the cap is set to 0.
    Task AssertUnderJobQuotaAsync(
        Guid organizationId,
        AiJobType jobType,
        CancellationToken ct = default);

    /// Throws QuotaExceededException if `organizationId` has hit the
    /// daily chat-message cap. Counts user-role chat_messages whose
    /// session belongs to a report owned by this org.
    Task AssertUnderChatQuotaAsync(
        Guid organizationId,
        CancellationToken ct = default);
}

/// Thrown by IQuotaService when a per-org daily cap would be exceeded.
/// API layer translates this into HTTP 429 with a Retry-After hint.
public sealed class QuotaExceededException : Exception
{
    public string Kind { get; }
    public int Limit { get; }

    public QuotaExceededException(string kind, int limit)
        : base($"Daily {kind} quota reached ({limit}/24h). Try again later.")
    {
        Kind = kind;
        Limit = limit;
    }
}
