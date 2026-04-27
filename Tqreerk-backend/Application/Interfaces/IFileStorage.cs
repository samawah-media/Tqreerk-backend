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

    /// Returns a URL the client can use to read the file. For public stores
    /// this is permanent; for private stores it's a short-lived signed URL.
    Task<string> GetReadUrlAsync(string objectKey, TimeSpan? lifetime = null, CancellationToken ct = default);

    /// Delete a previously stored object. No-op if it doesn't exist.
    Task DeleteAsync(string objectKey, CancellationToken ct = default);

    /// List all object keys under a prefix. Used to discover files written by
    /// external services (e.g. Cloud Translation v3, which mangles output
    /// filenames in non-obvious ways). Implementations may return at most
    /// `max` keys; default 100 is plenty for our use cases.
    Task<IReadOnlyList<string>> ListAsync(string prefix, int max = 100, CancellationToken ct = default);
}

public record StoredFile(string ObjectKey, long SizeBytes, string ContentType);
