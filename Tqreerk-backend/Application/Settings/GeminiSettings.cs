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
    /// Vertex AI location. Defaults to <c>global</c> — same as the
    /// ai-service's <c>vertex_location</c> setting — because it auto-
    /// routes every request to whichever region has the requested model
    /// and gives the highest aggregate quota of any single endpoint.
    /// Regional locations (e.g. <c>me-central1</c>) only carry a subset
    /// of Gemini models and would 404 on <c>gemini-2.5-flash*</c> in
    /// this project. If a contract ever pins data residency to a
    /// specific region, override per-deploy via the env var.
    /// </summary>
    public string Region { get; set; } = "global";

    /// <summary>
    /// Gemini model id (Vertex AI publisher: google). Defaults to the
    /// lite tier — short passages translate well at this size, latency
    /// is half of <c>gemini-2.5-flash</c>, cost is a fraction. Override
    /// per-env via <c>Gemini__Model</c> if a heavier tier is needed for
    /// quality on a specific deploy.
    /// </summary>
    public string Model { get; set; } = "gemini-2.5-flash-lite";

    /// <summary>HTTP timeout for the Vertex call (seconds). Short
    /// intentionally — translate-text is interactive UI; if Vertex
    /// hasn't responded in 30 s we'd rather fail loudly than hold the
    /// browser open.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
