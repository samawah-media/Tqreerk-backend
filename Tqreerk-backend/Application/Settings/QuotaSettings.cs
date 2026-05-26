namespace Taqreerk.Application.Settings;

/// Daily quotas for the AI surfaces that cost real money. Each cap is a
/// rolling 24-hour count. Value of 0 disables that specific cap. Tune via
/// appsettings or env var without redeploy.
///
/// Ingest / Translate are per-org because reports (and their costs) are
/// owned by an org. Chat is per-user because chat sessions are owned by
/// an individual and the agent's accessible scope is computed per-user
/// (Published OR own-org membership) — capping chat per org would punish
/// quiet orgs whose one chatty user ate the whole allowance.
public class QuotaSettings
{
    public const string Section = "Quota";

    /// Master switch — set false to bypass all quota checks.
    public bool Enabled { get; set; } = true;

    /// Max Ingestion jobs per org per 24h. Each ingest costs Gemini Vision
    /// per page + GPU time on the doc-processor.
    public int DailyIngestPerOrg { get; set; } = 20;

    /// Max Translation jobs per org per 24h. Google Cloud Translation API
    /// is per-character paid; multi-translating long PDFs adds up.
    public int DailyTranslatePerOrg { get; set; } = 10;

    /// Max chat user-messages per user per 24h. Each message also enqueues
    /// a Ragas eval (~3× extra Gemini calls) and runs an agent loop with
    /// up to 5 tool round-trips, so this is the primary chat-side cost
    /// gate.
    public int DailyChatPerUser { get; set; } = 200;
}
