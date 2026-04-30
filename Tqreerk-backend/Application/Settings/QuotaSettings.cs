namespace Taqreerk.Application.Settings;

/// Per-organization daily quotas for the AI surfaces that cost real money.
/// Each cap is a rolling 24-hour count over ai_jobs (or chat_messages for
/// the chat cap). A value of 0 disables that specific cap. Tune via
/// appsettings or env var without redeploy.
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

    /// Max chat user-messages per org per 24h. Each message also enqueues
    /// a Ragas eval (~6× extra Gemini calls), so this is the primary
    /// chat-side cost gate.
    public int DailyChatPerOrg { get; set; } = 500;
}
