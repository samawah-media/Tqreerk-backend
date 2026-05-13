namespace Taqreerk.Application.Settings;

/// <summary>
/// Bound to the "Gemini" configuration section. Used by the in-process
/// Vertex AI translator that powers <c>POST /api/ai/tools/translate-text</c>
/// — short passages from the PDF reader selection toolbar are translated
/// by the .NET host calling Vertex AI directly, with no ai-service hop.
///
/// Auth is Application Default Credentials (the Cloud Run runtime service
/// account, which has the <c>Vertex AI User</c> role). No API key is
/// required or accepted; that keeps the secret surface zero for this
/// feature.
///
/// For full-document translation, ingest, summarize, chat, etc., Gemini
/// is still invoked from the ai-service (Python) — those flows need the
/// chunking / RAG / job-queue machinery that lives there.
/// </summary>
public class GeminiSettings
{
    public const string Section = "Gemini";

    /// <summary>
    /// GCP project that owns the Vertex AI endpoint. Sourced from the
    /// same `GCP_PROJECT_ID` GitHub secret the deploy workflows already
    /// use, so no new secret is required at the org level.
    /// </summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>
    /// Vertex AI region. <c>me-central1</c> matches the project's default
    /// region and keeps the request in-region of the Cloud Run service —
    /// no cross-region hop on the hot path.
    /// </summary>
    public string Region { get; set; } = "me-central1";

    /// <summary>
    /// Gemini model id (Vertex AI publisher: google). Default favours
    /// the fast, cheap tier — short passages translate well at this size
    /// and p95 latency stays under ~1 s. Override per-env if a different
    /// tier is preferred.
    /// </summary>
    public string Model { get; set; } = "gemini-2.5-flash";

    /// <summary>HTTP timeout for the Vertex call (seconds). Short
    /// intentionally — translate-text is interactive UI; if Vertex
    /// hasn't responded in 30 s we'd rather fail loudly than hold the
    /// browser open.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
