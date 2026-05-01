using Taqreerk.Domain.Enums;

namespace Taqreerk.Application.Interfaces;

/// Daily quotas for AI operations. Ingest / Translate are scoped per-org
/// (the org owns the report being processed); chat is scoped per-user
/// (chat sessions are owned by an individual). Implementations count
/// rolling 24-hour usage and throw QuotaExceededException when the cap
/// is hit.
public interface IQuotaService
{
    /// Throws QuotaExceededException if `organizationId` has hit its daily
    /// cap for the given AI job type. No-op when quotas are disabled or
    /// the cap is set to 0.
    Task AssertUnderJobQuotaAsync(
        Guid organizationId,
        AiJobType jobType,
        CancellationToken ct = default);

    /// Throws QuotaExceededException if `userId` has hit the daily
    /// chat-message cap. Counts user-role chat_messages whose session is
    /// owned by this user.
    Task AssertUnderChatQuotaAsync(
        Guid userId,
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
