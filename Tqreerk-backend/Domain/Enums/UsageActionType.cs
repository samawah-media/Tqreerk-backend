namespace Taqreerk.Domain.Enums;

/// Per-user metered actions gated by the freemium plan limits. Each value
/// maps to a column on `plans` (e.g. ReportFullAccess + ReportDownload both
/// share `reports_download_limit`; AiTranslate + AiCompare share
/// `ai_calls_limit`; SaveReport has its own `saved_reports_limit`).
///
/// Persisted as a string in `usage_tracking.action_type` so log rows stay
/// readable even after enum reordering.
public enum UsageActionType
{
    ReportFullAccess,
    ReportDownload,
    AiTranslate,
    AiCompare,
    SaveReport,
}
