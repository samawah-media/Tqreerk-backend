using SkiaSharp;

namespace Taqreerk.Infrastructure.Storage;

/// <summary>
/// Produces the three cover-image variants (thumb / medium / full) that the
/// frontend renders via <c>srcset</c>. Each variant is width-bounded with
/// aspect ratio preserved and never up-scaled — sources smaller than a
/// target width are emitted at their native size to avoid wasting bytes
/// re-encoding a blurry blow-up.
///
/// Width targets are chosen to match common viewport breakpoints:
///   • thumb  — 320 px wide: list-card thumbnails on phones, dense grids;
///   • medium — 768 px wide: tablet hero, list-card on desktop;
///   • full   — 1280 px wide: detail-page hero on desktop, 2× DPR phones.
/// The browser picks the cheapest variant via <c>srcset</c>, so over-shooting
/// on the upper end costs us nothing for mobile users.
/// </summary>
public static class CoverImageVariants
{
    public const string ThumbName = "thumb.webp";
    public const string MediumName = "medium.webp";
    public const string FullName = "full.webp";

    private const int ThumbWidth = 320;
    private const int MediumWidth = 768;
    private const int FullWidth = 1280;

    /// <summary>
    /// Decode <paramref name="sourceBytes"/> once and emit three WebP byte
    /// payloads. Decoding once is materially cheaper than decoding three
    /// times on the upload hot path (typical cover is ~500 KB JPEG that
    /// SkiaSharp takes ~30 ms to decode).
    /// </summary>
    public static VariantPayloads Generate(byte[] sourceBytes)
    {
        if (sourceBytes is null || sourceBytes.Length == 0)
            throw new ArgumentException("Empty image payload.", nameof(sourceBytes));

        using var bitmap = SKBitmap.Decode(sourceBytes)
            ?? throw new ArgumentException(
                "Could not decode image — payload is not a supported image format.",
                nameof(sourceBytes));
        return Generate(bitmap);
    }

    /// <summary>
    /// Variant generation when the caller already has pixels in hand —
    /// used by the bulk-import PDF render path where PDFtoImage hands us
    /// an SKBitmap directly.
    /// </summary>
    public static VariantPayloads Generate(SKBitmap source)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));

        return new VariantPayloads(
            Thumb: ResizeAndEncode(source, ThumbWidth),
            Medium: ResizeAndEncode(source, MediumWidth),
            Full: ResizeAndEncode(source, FullWidth));
    }

    private static byte[] ResizeAndEncode(SKBitmap source, int targetWidth)
    {
        // Never up-scale: a 240px source as `medium` stays at 240px. Saves
        // bytes on the wire and avoids manufacturing detail that isn't
        // there. The browser's srcset picker is happy with mismatched
        // widths.
        if (source.Width <= targetWidth)
            return CoverImageEncoder.EncodeBitmapAsWebp(source);

        var targetHeight = (int)Math.Round(source.Height * (double)targetWidth / source.Width);
        // SkiaSharp 2.88 still uses the SKFilterQuality enum (the
        // SKSamplingOptions API was added in 3.x). High is the lanczos-ish
        // resampler that gives the best perceived quality for photographic
        // content at downscale ratios > 2× — which is every variant we
        // generate from a typical multi-MB upload.
        var info = new SKImageInfo(targetWidth, targetHeight);
        using var resized = source.Resize(info, SKFilterQuality.High)
            ?? throw new InvalidOperationException(
                $"SkiaSharp resize to {targetWidth}x{targetHeight} returned null.");
        return CoverImageEncoder.EncodeBitmapAsWebp(resized);
    }

    /// <summary>
    /// The three encoded WebP payloads, ready for upload. Caller is
    /// responsible for picking the storage backend (local vs GCS public).
    /// </summary>
    public record VariantPayloads(byte[] Thumb, byte[] Medium, byte[] Full);
}
