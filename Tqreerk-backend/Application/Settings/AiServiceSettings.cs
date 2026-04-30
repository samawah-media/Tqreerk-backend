namespace Taqreerk.Application.Settings;

public class AiServiceSettings
{
    public const string Section = "AiService";

    /// Base URL of the FastAPI ai-service deployment, including the /api/ai prefix.
    /// Example: https://taqreerk-ai-service-staging-912038409401.me-central1.run.app/api/ai
    public string BaseUrl { get; set; } = string.Empty;

    /// Per-call timeout in seconds. /ingest can run for a few minutes for large
    /// PDFs, /summarize is faster, /translate sits in the middle.
    public int TimeoutSeconds { get; set; } = 300;

    /// Background worker poll interval — how often we look for Pending AiJobs.
    public int WorkerPollSeconds { get; set; } = 5;

    /// Bucket prefix for translated PDFs. The ai-service writes the output PDF
    /// here; we store the returned URL in ReportTranslation.TranslatedFileUrl.
    /// Example: gs://taqreerk-uploads/taqreerk-uploads-dev/translations
    public string TranslationOutputPrefix { get; set; } = string.Empty;

    /// /reports/ingest is fire-and-forget on the AI service — the .NET worker
    /// polls /reports/jobs/{job_id} until the job reaches Completed/Failed.
    /// These two knobs control that polling loop. A 50-page Arabic PDF takes
    /// 3–10 min, so the default ceiling is generous.
    public int IngestPollSeconds { get; set; } = 5;
    public int IngestPollMaxMinutes { get; set; } = 30;
}
