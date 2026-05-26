using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Settings;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

/// Counts rolling 24-hour usage and throws QuotaExceededException when
/// a daily cap is hit. Thin and stateless — the only state is the
/// (kind → cap) config. Ingest / Translate are scoped per-org; chat is
/// scoped per-user (chat_sessions.UserId is the natural owner of a chat).
///
/// Failure semantics: any non-quota error inside the count query is
/// LOGGED AND TREATED AS UNDER-QUOTA. We refuse to lock real users out
/// because of a transient DB hiccup; the next attempt re-runs the count.
public class QuotaService : IQuotaService
{
    private readonly TaqreerkDbContext _db;
    private readonly QuotaSettings _settings;
    private readonly ILogger<QuotaService> _logger;

    public QuotaService(
        TaqreerkDbContext db,
        IOptions<QuotaSettings> settings,
        ILogger<QuotaService> logger)
    {
        _db = db;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task AssertUnderJobQuotaAsync(
        Guid organizationId,
        AiJobType jobType,
        CancellationToken ct = default)
    {
        if (!_settings.Enabled) return;

        var (cap, label) = jobType switch
        {
            AiJobType.Ingestion   => (_settings.DailyIngestPerOrg,   "ingest"),
            AiJobType.Translation => (_settings.DailyTranslatePerOrg, "translate"),
            // Internal job types (Evaluation) are uncapped — gated upstream
            // by the chat quota.
            _ => (0, jobType.ToString().ToLowerInvariant()),
        };
        if (cap <= 0) return;

        var since = DateTime.UtcNow.AddHours(-24);

        int count;
        try
        {
            count = await _db.AiJobs
                .Where(j => j.OrganizationId == organizationId
                         && j.JobType == jobType
                         && j.CreatedAt > since)
                .CountAsync(ct);
        }
        catch (Exception exc)
        {
            // Fail-open on count errors — see file docstring.
            _logger.LogWarning(
                exc,
                "[quota] count failed for org={OrgId} kind={Kind} — allowing through",
                organizationId, jobType);
            return;
        }

        if (count >= cap)
        {
            _logger.LogWarning(
                "[quota] org={OrgId} hit daily {Kind} cap ({Count}/{Cap}) — throwing 429",
                organizationId, label, count, cap);
            throw new QuotaExceededException(label, cap);
        }
    }

    public async Task AssertUnderChatQuotaAsync(
        Guid userId,
        CancellationToken ct = default)
    {
        if (!_settings.Enabled) return;
        var cap = _settings.DailyChatPerUser;
        if (cap <= 0) return;

        var since = DateTime.UtcNow.AddHours(-24);

        int count;
        try
        {
            // Join chat_messages → chat_sessions on UserId. The
            // chat_sessions.UserId index covers this lookup.
            count = await (
                from m in _db.ChatMessages
                join s in _db.ChatSessions on m.SessionId equals s.Id
                where s.UserId == userId
                   && m.Role == "user"
                   && m.CreatedAt > since
                select m.Id
            ).CountAsync(ct);
        }
        catch (Exception exc)
        {
            _logger.LogWarning(
                exc,
                "[quota] chat count failed for user={UserId} — allowing through",
                userId);
            return;
        }

        if (count >= cap)
        {
            _logger.LogWarning(
                "[quota] user={UserId} hit daily chat cap ({Count}/{Cap}) — throwing 429",
                userId, count, cap);
            throw new QuotaExceededException("chat message", cap);
        }
    }
}
