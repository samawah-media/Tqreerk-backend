using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.API.Filters;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Enums;

namespace Taqreerk.API.Controllers;

/// <summary>
/// Thin proxy in front of the Python ai-service's /tools/* routes. Same
/// rationale as ChatController: the ai-service has no auth of its own and
/// is not reachable from the browser in production. The .NET layer enforces
/// JWT auth + freemium quota and forwards the user-supplied text payload
/// via the typed AiServiceClient.
///
/// Unlike chat (SSE, streaming), tools are short synchronous JSON RPCs, so
/// we use IAiServiceClient instead of a raw HttpClient.
/// </summary>
[ApiController]
[Route("api/ai/tools")]
[Authorize]
public class AiToolsController : ControllerBase
{
    // Matches Python's TranslateTextRequest cap. Validating here avoids a
    // round-trip just to get a 422 back.
    private const int MaxTextLength = 5000;

    private readonly IAiServiceClient _ai;
    private readonly ILogger<AiToolsController> _logger;

    public AiToolsController(IAiServiceClient ai, ILogger<AiToolsController> logger)
    {
        _ai = ai;
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
            var result = await _ai.TranslateTextAsync(req.Text, target, ct);
            return Ok(new TranslateTextResponse(result.TranslatedText, result.TargetLanguage));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            // AiServiceClient.PostAsync wraps non-2xx + transport failures as
            // InvalidOperationException with the upstream body in the message.
            // ExceptionHandlingMiddleware's default mapping for that exception
            // type is 409, which is wrong here — surface a 502 so the
            // frontend can show "translation service unavailable" cleanly.
            _logger.LogWarning(ex, "[ai-tools] translate-text upstream failed");
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
