using SkiaSharp;

namespace Taqreerk.Infrastructure.Storage;

/// <summary>
/// Centralises the "every cover image is .webp" rule. Both the manual
/// upload flow (org users picking a JPEG/PNG/WEBP) and the bulk-import
/// PDF-to-cover renderer go through this helper so the stored object on
/// GCS is always a WebP — same content-type, same extension, same size
/// expectations downstream.
///
/// WebP gives us roughly 25-35% smaller payloads than JPEG at the same
/// perceived quality, with universal browser support today. The decode
/// path is permissive — SkiaSharp handles JPEG, PNG, WEBP, BMP, GIF,
/// HEIF natively, so the org user never has to think about format.
/// </summary>
public static class CoverImageEncoder
{
    /// <summary>WebP quality in [0, 100]. 80 is the sweet spot for
    /// thumbnails — visually lossless at this size.</summary>
    private const int Quality = 80;

    public const string ContentType = "image/webp";
    public const string Extension = ".webp";

    /// <summary>
    /// Decode whatever bytes the caller has in memory, then re-encode
    /// as WebP. Returns the encoded bytes. Throws on a non-image input
    /// — the manual-upload path should already have content-type
    /// validation upstream, this exception just surfaces a malformed
    /// payload that snuck past it.
    /// </summary>
    public static byte[] EncodeWebp(byte[] sourceBytes)
    {
        if (sourceBytes is null || sourceBytes.Length == 0)
            throw new ArgumentException("Empty image payload.", nameof(sourceBytes));

        using var ms = new MemoryStream(sourceBytes);
        return EncodeWebp(ms);
    }

    /// <summary>
    /// Stream variant of <see cref="EncodeWebp(byte[])"/>. Reads the
    /// stream fully (Skia's decoder needs random access), then runs
    /// the same re-encode pipeline.
    /// </summary>
    public static byte[] EncodeWebp(Stream sourceStream)
    {
        if (sourceStream is null) throw new ArgumentNullException(nameof(sourceStream));

        using var skStream = new SKManagedStream(sourceStream, disposeManagedStream: false);
        using var bitmap = SKBitmap.Decode(skStream)
            ?? throw new ArgumentException(
                "Could not decode image — payload is not a supported image format.",
                nameof(sourceStream));
        return EncodeBitmapAsWebp(bitmap);
    }

    /// <summary>
    /// Encode a SkiaSharp bitmap (typically the result of an in-process
    /// render — e.g. PDFtoImage on the bulk-import path) as WebP. Skips
    /// the decode step since the caller already has pixels in hand.
    /// </summary>
    public static byte[] EncodeBitmapAsWebp(SKBitmap bitmap)
    {
        if (bitmap is null) throw new ArgumentNullException(nameof(bitmap));

        using var img = SKImage.FromBitmap(bitmap);
        using var data = img.Encode(SKEncodedImageFormat.Webp, Quality)
            ?? throw new InvalidOperationException("WebP encoding failed.");
        return data.ToArray();
    }

    /// <summary>
    /// Replace whatever extension the caller's filename has with .webp.
    /// Used to keep the GCS object key honest about the encoded format
    /// regardless of what the user originally uploaded.
    /// </summary>
    public static string WithWebpExtension(string? originalFileName)
    {
        var stem = string.IsNullOrWhiteSpace(originalFileName)
            ? "cover"
            : Path.GetFileNameWithoutExtension(originalFileName);
        // Path.GetFileNameWithoutExtension returns "" for inputs like
        // ".jpg" — defend against that producing "..webp".
        if (string.IsNullOrWhiteSpace(stem)) stem = "cover";
        return stem + Extension;
    }
}
