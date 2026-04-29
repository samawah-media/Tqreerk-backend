using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Options;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Settings;

namespace Taqreerk.Infrastructure.Storage;

/// Google Cloud Storage backend. Stores objects in the configured bucket and
/// generates short-lived V4 signed URLs for reads. Service account credentials
/// are loaded from a JSON file path or fall back to Application Default Credentials.
public class GcsFileStorage : IFileStorage
{
    private readonly FileStorageSettings _settings;
    private readonly StorageClient _storage;
    private readonly UrlSigner _signer;
    private readonly ILogger<GcsFileStorage> _logger;

    public GcsFileStorage(IOptions<FileStorageSettings> settings, ILogger<GcsFileStorage> logger)
    {
        _settings = settings.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(_settings.GcsBucketName))
            throw new InvalidOperationException("FileStorage:GcsBucketName must be set when Provider=gcs.");

        // Either an explicit service-account JSON path, or fall back to ADC.
        GoogleCredential credential;
        if (!string.IsNullOrWhiteSpace(_settings.GcsCredentialsJsonPath))
        {
            if (!File.Exists(_settings.GcsCredentialsJsonPath))
                throw new InvalidOperationException(
                    $"GCS credentials file not found at: {_settings.GcsCredentialsJsonPath}");

            // Local-dev only path (Cloud Run uses ADC via the service account).
            // FromStream is marked obsolete by Google.Apis.Auth; the suggested
            // CredentialFactory replacement requires async setup we can't run
            // in a constructor, so we keep this and silence the warning.
#pragma warning disable CS0618
            using var keyStream = File.OpenRead(_settings.GcsCredentialsJsonPath);
            credential = GoogleCredential.FromStream(keyStream);
#pragma warning restore CS0618
        }
        else
        {
            credential = GoogleCredential.GetApplicationDefault();
        }

        _storage = StorageClient.Create(credential);
        _signer = UrlSigner.FromCredential(credential);
    }

    public async Task<StoredFile> UploadAsync(
        Stream content,
        string originalFileName,
        string contentType,
        string folder,
        CancellationToken ct = default)
    {
        var safeFolder = SanitizeFolder(folder);
        var extension = Path.GetExtension(originalFileName);
        var fileName = $"{Guid.NewGuid():N}{extension}";
        // Optional bucket-level prefix — keeps dev/staging/prod inside the same
        // bucket isolated under their own root folders.
        var prefix = SanitizeFolder(_settings.GcsBucketPrefix);
        var withPrefix = string.IsNullOrEmpty(prefix) ? safeFolder
            : string.IsNullOrEmpty(safeFolder) ? prefix
            : $"{prefix}/{safeFolder}";
        var objectKey = string.IsNullOrEmpty(withPrefix) ? fileName : $"{withPrefix}/{fileName}";

        var obj = await _storage.UploadObjectAsync(
            bucket: _settings.GcsBucketName,
            objectName: objectKey,
            contentType: contentType,
            source: content,
            cancellationToken: ct);

        var size = obj.Size.HasValue ? (long)obj.Size.Value : 0L;
        _logger.LogInformation("Uploaded gs://{Bucket}/{Key} ({Size} bytes)", _settings.GcsBucketName, objectKey, size);
        return new StoredFile(objectKey, size, contentType);
    }

    public async Task<string> GetReadUrlAsync(string objectKey, TimeSpan? lifetime = null, CancellationToken ct = default)
    {
        var window = lifetime ?? TimeSpan.FromMinutes(_settings.SignedUrlMinutes);

        // Bake response-content-type + response-content-disposition into the
        // signed URL itself. GCS V4 signs ALL query parameters at signing time,
        // so these have to be present in the RequestTemplate — appending them
        // afterwards on the frontend triggers SignatureDoesNotMatch.
        //
        // Why we need them: the original PDF is uploaded as application/pdf
        // (renders inline correctly), but Cloud Translation v3 stores the
        // translated PDF as application/octet-stream — that forces a download.
        // Overriding the response Content-Type at sign time normalises both
        // cases so the browser PDF viewer always opens them.
        var contentType = GuessContentType(objectKey);
        var queryParams = new Dictionary<string, IEnumerable<string>>
        {
            ["response-content-type"] = new[] { contentType },
            ["response-content-disposition"] = new[] { "inline" },
        };

        var template = UrlSigner.RequestTemplate
            .FromBucket(_settings.GcsBucketName)
            .WithObjectName(objectKey)
            .WithHttpMethod(HttpMethod.Get)
            .WithQueryParameters(queryParams);

        var options = UrlSigner.Options.FromDuration(window);

        var url = await _signer.SignAsync(template, options, ct);
        return url;
    }

    /// Best-effort Content-Type from the object key extension. The override
    /// only matters when GCS's stored Content-Type is wrong (e.g. octet-stream
    /// from Cloud Translation v3); for everything else we still set the right
    /// type, which is harmless.
    private static string GuessContentType(string objectKey)
    {
        var ext = Path.GetExtension(objectKey).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream",
        };
    }

    public async Task DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        try
        {
            await _storage.DeleteObjectAsync(_settings.GcsBucketName, objectKey, cancellationToken: ct);
            _logger.LogInformation("Deleted gs://{Bucket}/{Key}", _settings.GcsBucketName, objectKey);
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Idempotent delete: missing object is fine.
        }
    }

    public async Task<IReadOnlyList<string>> ListAsync(string prefix, int max = 100, CancellationToken ct = default)
    {
        var keys = new List<string>();
        var options = new Google.Cloud.Storage.V1.ListObjectsOptions { PageSize = Math.Min(max, 1000) };
        await foreach (var obj in _storage
            .ListObjectsAsync(_settings.GcsBucketName, prefix, options)
            .WithCancellation(ct))
        {
            keys.Add(obj.Name);
            if (keys.Count >= max) break;
        }
        return keys;
    }

    private static string SanitizeFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return string.Empty;
        var cleaned = new string(folder
            .Trim('/', '\\')
            .Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '/' ? c : '-')
            .ToArray());
        return cleaned;
    }
}
