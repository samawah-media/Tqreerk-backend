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
    public string GcsBucketName { get; set; } = string.Empty;
    /// Optional top-level folder inside the bucket (e.g. "taqreerk-uploads-dev")
    /// so dev/staging/prod can share a bucket without colliding.
    public string GcsBucketPrefix { get; set; } = string.Empty;
    /// Path to a service-account JSON file. If empty, falls back to Application
    /// Default Credentials (GOOGLE_APPLICATION_CREDENTIALS env var).
    public string GcsCredentialsJsonPath { get; set; } = string.Empty;
    /// Default lifetime of generated signed URLs.
    public int SignedUrlMinutes { get; set; } = 60;
}
