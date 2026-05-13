using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.API.Filters;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Enums;

namespace Taqreerk.API.Controllers;

/// <summary>
/// Short-passage AI helpers for the PDF reader. Unlike <see cref="ChatController"/>
/// (which proxies to the Python ai-service because chat needs LangGraph
/// + RAG + groundedness + caches), translate-text is a single Gemini RPC
/// and we call Gemini directly from .NET via <see cref="IGeminiTextTranslator"/>.
/// Drops one Cloud-Run-to-Cloud-Run hop on a hot interactive path and
/// removes ai-service availability as a SPOF for this feature.
/// </summary>
[ApiController]
[Route("api/ai/tools")]
[Authorize]
public class AiToolsController : ControllerBase
{
    // Validating here avoids paying for a Gemini round-trip just to discover
    // the input was too long. The 5000-char cap matches the original Python
    // tools.py contract so the frontend doesn't have to know which path it
    // hit if we ever bring ai-service back into the loop.
    private const int MaxTextLength = 5000;

    private readonly IGeminiTextTranslator _translator;
    private readonly ILogger<AiToolsController> _logger;

    public AiToolsController(
        IGeminiTextTranslator translator,
        ILogger<AiToolsController> logger)
    {
        _translator = translator;
        _logger = logger;
    }

    /// <summary>Translate a short selection (≤ 5000 chars) via Gemini. Used
    /// by the PDF-reader selection-toolbar's Translate bubble. Counts
    /// against the same `AiTranslate` quota as the full-document translate
    /// so the existing LimitExceededModal lights up at the cap.</summary>
    [HttpPost("translate-text")]
    [EnforceUsageLimit(UsageActionType.AiTranslate)]
    [ProducesResponseType(typeof(TranslateTextResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> TranslateText(
        [FromBody] TranslateTextRequest req,
        CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Text))
            return BadRequest(new { error = "text is required" });
        if (req.Text.Length > MaxTextLength)
            return BadRequest(new { error = $"text exceeds {MaxTextLength}-character limit" });

        var target = string.IsNullOrWhiteSpace(req.TargetLanguage)
            ? "en"
            : req.TargetLanguage.Trim().ToLowerInvariant();

        try
        {
            var translated = await _translator.TranslateAsync(req.Text, target, ct);
            return Ok(new TranslateTextResponse(translated, target));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // GeminiTextTranslator throws InvalidOperationException on non-2xx
            // and HttpRequestException on transport failure. TaskCanceledException
            // covers the per-request timeout. ExceptionHandlingMiddleware's
            // default for InvalidOperationException is 409 (Conflict) which is
            // wrong for an upstream failure — return 502 explicitly so the
            // frontend shows "translation service unavailable" cleanly.
            _logger.LogWarning(ex, "[ai-tools] translate-text failed");
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "translation service unavailable" });
        }
    }

    public record TranslateTextRequest(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("target_language")] string? TargetLanguage = null);

    public record TranslateTextResponse(
        [property: JsonPropertyName("translated_text")] string TranslatedText,
        [property: JsonPropertyName("target_language")] string TargetLanguage);
}
