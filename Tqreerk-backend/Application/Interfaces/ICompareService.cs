using Taqreerk.Application.DTOs.Compare;

namespace Taqreerk.Application.Interfaces;

/// AI-powered report comparison surface. Wraps three concerns:
///   1. Validating that the caller picked 2..4 published reports.
///   2. Cache lookup — same (user, sorted-ids) → reuse the prior
///      ReportComparison row rather than burning Gemini quota.
///   3. Calling the Python ai-service, persisting the row, returning
///      the rich comparison view.
///
/// The freemium gate is applied via [EnforceUsageLimit(AiCompare)] on
/// the controller; the cache hit short-circuits before the gate so
/// "view a comparison I already paid for" doesn't double-charge.
public interface ICompareService
{
    /// Run a comparison (or return the cached one). Throws
    /// ArgumentException for bad input, KeyNotFoundException when a
    /// report id doesn't exist or isn't accessible.
    Task<ComparisonResultDto> CompareAsync(
        Guid userId, IReadOnlyList<Guid> reportIds, CancellationToken ct = default);

    /// History list for the caller (newest first). Used by the
    /// "اخترنا لك" / "مقارناتي" placeholder views — not currently in
    /// the UI but the endpoint stays so we can surface it later.
    Task<IReadOnlyList<ComparisonListItemDto>> ListMineAsync(
        Guid userId, int take = 20, CancellationToken ct = default);

    /// Re-render a stored comparison without re-running the AI. The
    /// caller must own the row (UserId match), otherwise 404.
    Task<ComparisonResultDto> GetMineAsync(
        Guid userId, Guid comparisonId, CancellationToken ct = default);
}
