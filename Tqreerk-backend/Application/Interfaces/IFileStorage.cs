namespace Taqreerk.Application.Interfaces;

/// Abstraction over the binary store. The concrete implementation (local disk,
/// GCS, S3, etc.) is chosen via DI based on configuration.
public interface IFileStorage
{
    /// Upload a file and return the persisted object key. The key is what we
    /// store on the entity (e.g. OrganizationFile.FileUrl); to actually serve
    /// the file we resolve it via GetReadUrlAsync.
    Task<StoredFile> UploadAsync(
        Stream content,
        string originalFileName,
        string contentType,
        string folder,
        CancellationToken ct = default);

    /// Upload a file destined to be served publicly with a long-lived
    /// browser cache. Used by the cover-variant pipeline so the LCP hero
    /// image is fetched directly from GCS (no signing round-trip) and
    /// cached at the edge / browser for a year. On GCS:
    ///   • PredefinedAcl = PublicRead (works only when uniform bucket-level
    ///     access is OFF; ops note in production-db-setup.md);
    ///   • Cache-Control = "public, max-age=31536000, immutable" so the
    ///     browser never re-validates within the TTL.
    /// On LocalFileStorage the call is structurally identical — the file
    /// is just dropped into the same public-served directory.
    Task<StoredFile> UploadPublicAsync(
        Stream content,
        string originalFileName,
        string contentType,
        string folder,
        CancellationToken ct = default);

    /// Returns a URL the client can use to read the file. For public stores
    /// this is permanent; for private stores it's a short-lived signed URL.
    Task<string> GetReadUrlAsync(string objectKey, TimeSpan? lifetime = null, CancellationToken ct = default);

    /// Returns the canonical, signature-free URL for an object that was
    /// uploaded via <see cref="UploadPublicAsync"/>. No round-trip needed —
    /// the implementation just templates the bucket + key. Throws if the
    /// configured backend has no notion of public URLs.
    string GetPublicUrl(string objectKey);

    /// Delete a previously stored object. No-op if it doesn't exist.
    Task DeleteAsync(string objectKey, CancellationToken ct = default);

    /// List all object keys under a prefix. Used to discover files written by
    /// external services (e.g. Cloud Translation v3, which mangles output
    /// filenames in non-obvious ways). Implementations may return at most
    /// `max` keys; default 100 is plenty for our use cases.
    Task<IReadOnlyList<string>> ListAsync(string prefix, int max = 100, CancellationToken ct = default);
}

public record StoredFile(string ObjectKey, long SizeBytes, string ContentType);
