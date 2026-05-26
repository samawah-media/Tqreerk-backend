namespace Taqreerk.Application.Interfaces;

/// <summary>
/// Translates a short passage of text using Gemini, called in-process
/// (no ai-service hop). Used by the PDF-reader selection-toolbar bubble.
/// Implementation talks directly to the Gemini API.
/// </summary>
public interface IGeminiTextTranslator
{
    /// <summary>
    /// Translate <paramref name="text"/> into <paramref name="targetLanguage"/>
    /// (ISO 639-1 code, e.g. "en"). Returns the translated string with no
    /// surrounding quotes or commentary. Throws on transport / API / safety-
    /// filter failures — the controller maps those to 502.
    /// </summary>
    Task<string> TranslateAsync(
        string text,
        string targetLanguage,
        CancellationToken ct = default);
}
