namespace Taqreerk.Domain.Enums;

/// Per-user metered actions gated by the freemium plan limits. Each value
/// maps to a column on `plans` (Ai*Limit / IndividualReadsLimit /
/// IndividualSavedReportsLimit / IndividualDownloadsLimit).
///
/// Persisted as a string in `usage_tracking.action_type` so log rows stay
/// readable even after enum reordering. Adding new values is safe;
/// renaming requires a data backfill on the existing rows.
///
/// AI counters live alongside the read/save/download counters because
/// they share the same monthly-cap mechanic — the only difference is
/// which Plan column resolves the cap (see UsageService.ResolveLimit).
/// Pro-only AI features (Trend Analysis, Knowledge Graph, etc.) are
/// gated by boolean flags on Plan, NOT counter values, so they don't
/// appear in this enum.
public enum UsageActionType
{
    ReportFullAccess,
    ReportDownload,
    SaveReport,

    // ── AI — each tracked independently ───────────────────────────────
    AiSummarize,
    AiKeyFindings,
    AiTranslate,
    AiSimilarSuggestions,
    AiCompare,

    /// Report-page AI chat (trial tier: 1 message/month; paid tiers: unlimited).
    AiChat,
}
