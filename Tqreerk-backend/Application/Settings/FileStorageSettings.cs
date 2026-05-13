namespace Taqreerk.Application.Settings;

public class FileStorageSettings
{
    public const string Section = "FileStorage";

    /// "local" or "gcs". When unset or "local", LocalFileStorage is used.
    public string Provider { get; set; } = "local";

    // ── Local storage ────────────────────────────────────────────────────────
    /// Filesystem path (absolute or relative to ContentRootPath) where uploads
    /// are stored. Files are served at {LocalPublicBaseUrl}/{objectKey}.
    public string LocalRoot { get; set; } = "uploads";
    public string LocalPublicBaseUrl { get; set; } = "/uploads";

    // ── GCS ──────────────────────────────────────────────────────────────────
    /// Primary (private) bucket. Holds report PDFs, legacy covers,
    /// translations, org files — everything served via signed URLs.
    /// Public Access Prevention must stay Enforced on this bucket.
    public string GcsBucketName { get; set; } = string.Empty;
    /// Optional top-level folder inside <see cref="GcsBucketName"/>
    /// (e.g. "taqreerk-uploads-dev") so dev/staging/prod can share a
    /// bucket without colliding.
    public string GcsBucketPrefix { get; set; } = string.Empty;
    /// Separate bucket for cover-variant uploads. Required when the
    /// variants pipeline is enabled; <see cref="UploadPublicAsync"/>
    /// targets this bucket exclusively. Keeping it apart from
    /// <see cref="GcsBucketName"/> means the primary bucket can remain
    /// PAP-Enforced + UBLA-locked; the public bucket gets a bucket-level
    /// allUsers grant and any catastrophic misconfiguration only exposes
    /// cover images (already meant for the public web).
    /// When empty, public cover uploads throw — callers should treat the
    /// feature as opt-in via configuration.
    public string GcsPublicBucketName { get; set; } = string.Empty;
    /// Path to a service-account JSON file. If empty, falls back to Application
    /// Default Credentials (GOOGLE_APPLICATION_CREDENTIALS env var).
    public string GcsCredentialsJsonPath { get; set; } = string.Empty;
    /// Default lifetime of generated signed URLs.
    public int SignedUrlMinutes { get; set; } = 60;
}
