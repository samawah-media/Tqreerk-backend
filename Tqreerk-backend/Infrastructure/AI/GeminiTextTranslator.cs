using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Settings;

namespace Taqreerk.Infrastructure.AI;

/// <summary>
/// In-process Vertex AI translator for short-passage text translation.
/// Hits the regional aiplatform endpoint with an ADC-issued bearer token
/// — no API key, no shared secret. Cloud Run's runtime service account
/// already has the Vertex AI User role.
///
/// Why bypass the ai-service for this:
///   • The endpoint does no chunking, no embedding, no RAG — none of the
///     machinery that lives in Python is needed.
///   • The extra hop costs ~50-150 ms of inter-Cloud-Run latency for
///     what is otherwise a single Gemini RPC. Direct halves the budget.
///   • One fewer SPOF for an interactive UI feature: a degraded
///     ai-service no longer takes selection-translate down with it.
///
/// Why keep ai-service for the other Gemini-backed flows:
///   • Chat needs LangGraph + chunk retrieval + groundedness + caches.
///   • Ingest / summarize / compare / full-document translate need the
///     report_chunks pipeline. None of that ports cleanly to .NET.
/// </summary>
public class GeminiTextTranslator : IGeminiTextTranslator
{
    // Vertex AI requires the broad cloud-platform scope; aiplatform-only
    // scopes don't exist for the publisher models surface. The Cloud Run
    // SA's IAM role still bounds what the token can actually do.
    private const string VertexScope = "https://www.googleapis.com/auth/cloud-platform";

    private readonly HttpClient _http;
    private readonly GeminiSettings _settings;
    private readonly GoogleCredential _credential;
    private readonly ILogger<GeminiTextTranslator> _logger;

    public GeminiTextTranslator(
        HttpClient http,
        IOptions<GeminiSettings> settings,
        ILogger<GeminiTextTranslator> logger)
    {
        _http = http;
        _settings = settings.Value;
        _logger = logger;

        // ADC: on Cloud Run picks up the runtime SA via the metadata
        // server; locally falls back to gcloud auth application-default
        // login OR a service-account JSON file pointed at by
        // GOOGLE_APPLICATION_CREDENTIALS. Scoped here so the underlying
        // token cache reuses the right token across all calls.
        _credential = GoogleCredential.GetApplicationDefault()
            .CreateScoped(VertexScope);

        // BaseAddress depends on the Vertex location:
        //   • "global"  → https://aiplatform.googleapis.com/
        //   • {region}  → https://{region}-aiplatform.googleapis.com/
        // Regional hosts only serve a subset of Gemini models; the
        // global endpoint routes to whichever region has the model, so
        // it's the default and the only one we expect to use in prod.
        // Trailing slash so relative routes resolve cleanly.
        var host = _settings.Region.Equals("global", StringComparison.OrdinalIgnoreCase)
            ? "https://aiplatform.googleapis.com/"
            : $"https://{_settings.Region}-aiplatform.googleapis.com/";
        _http.BaseAddress = new Uri(host);
        _http.Timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds);
    }

    public async Task<string> TranslateAsync(
        string text, string targetLanguage, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.ProjectId))
            throw new InvalidOperationException(
                "Gemini:ProjectId is not configured. Set GCP_PROJECT_ID via " +
                "Gemini__ProjectId on the Cloud Run service.");

        // Mirror the Python tools.py prompt verbatim so callers get the
        // same output style regardless of which path they hit. Returning
        // ONLY the translated text matters — the frontend's toolbar
        // shows the raw response in a bubble; any preface text leaks.
        var langLabel = targetLanguage.Equals("en", StringComparison.OrdinalIgnoreCase)
            ? "English"
            : targetLanguage;
        var prompt =
            $"Translate the following text into {langLabel}. " +
            "Return only the translated text, with no explanation, no quotes, " +
            "and no commentary.\n\n" +
            text;

        var body = new
        {
            contents = new[]
            {
                new { parts = new[] { new { text = prompt } } },
            },
            generationConfig = new
            {
                // temperature=0.2 matches ai-service's "deterministic factual
                // output" convention. maxOutputTokens=2048 covers the worst
                // case of a 5000-char input expanding by ~30% in Arabic→English.
                temperature = 0.2,
                maxOutputTokens = 2048,
            },
        };

        // Vertex AI publisher endpoint. The path is relative to BaseAddress
        // so the regional host (set in the constructor) doesn't have to be
        // re-templated per call.
        var url =
            $"v1/projects/{_settings.ProjectId}" +
            $"/locations/{_settings.Region}" +
            $"/publishers/google/models/{_settings.Model}" +
            $":generateContent";

        // Acquire a fresh access token. The underlying credential caches
        // and refreshes internally — calling this per-request is cheap.
        string accessToken;
        try
        {
            accessToken = await _credential.UnderlyingCredential
                .GetAccessTokenForRequestAsync(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[gemini-translate] ADC token fetch failed");
            throw new InvalidOperationException(
                "Failed to obtain a Vertex AI access token via ADC. Ensure the " +
                "runtime service account has the Vertex AI User role.", ex);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var startedAt = DateTimeOffset.UtcNow;
        using var resp = await _http.SendAsync(request, ct);
        var content = await resp.Content.ReadAsStringAsync(ct);
        var elapsedMs = (int)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "[gemini-translate] {Status} ({Elapsed}ms) body={Body}",
                (int)resp.StatusCode, elapsedMs, content);
            throw new InvalidOperationException(
                $"Vertex AI returned {(int)resp.StatusCode}: {Truncate(content, 500)}");
        }

        // Parse: { candidates: [{ content: { parts: [{ text }] }, finishReason }], ... }
        // The shape is stable across Gemini 1.x/2.x. If a future model
        // shifts it we'd see a parse error here, which is preferable to
        // silently emitting "" to the user.
        using var doc = JsonDocument.Parse(content);
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array
            || candidates.GetArrayLength() == 0)
        {
            // Block from the safety filter at the top level usually lands
            // here — promptFeedback is on the root, candidates are absent.
            var blockReason = ExtractBlockReason(doc.RootElement);
            _logger.LogWarning(
                "[gemini-translate] no candidates returned ({Elapsed}ms, block={Block})",
                elapsedMs, blockReason ?? "unknown");
            throw new InvalidOperationException(
                blockReason is not null
                    ? $"Gemini blocked the request: {blockReason}"
                    : "Gemini returned no candidates.");
        }

        var candidate = candidates[0];

        // Per-candidate safety stop: finishReason tells us if the model
        // started generating then got blocked mid-stream. We treat all
        // non-STOP terminations as failures rather than partial output.
        if (candidate.TryGetProperty("finishReason", out var fr))
        {
            var reason = fr.GetString();
            if (reason is "SAFETY" or "RECITATION" or "BLOCKLIST"
                or "PROHIBITED_CONTENT" or "SPII")
            {
                _logger.LogWarning(
                    "[gemini-translate] candidate blocked: finishReason={Reason}", reason);
                throw new InvalidOperationException(
                    $"Gemini blocked the response: finishReason={reason}");
            }
        }

        var partsText = candidate
            .GetProperty("content")
            .GetProperty("parts");
        if (partsText.ValueKind != JsonValueKind.Array || partsText.GetArrayLength() == 0)
            throw new InvalidOperationException("Gemini returned an empty parts array.");

        var translated = partsText[0].GetProperty("text").GetString() ?? string.Empty;
        translated = translated.Trim();

        _logger.LogInformation(
            "[gemini-translate] {InLen} → {OutLen} chars in {Elapsed}ms (lang={Lang})",
            text.Length, translated.Length, elapsedMs, targetLanguage);

        return translated;
    }

    private static string? ExtractBlockReason(JsonElement root)
    {
        if (!root.TryGetProperty("promptFeedback", out var pf)) return null;
        if (pf.TryGetProperty("blockReason", out var br)) return br.GetString();
        return null;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
