using Google.Apis.Auth.OAuth2;
using Google.Cloud.Iam.Credentials.V1;
using Google.Cloud.Storage.V1;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Settings;

namespace Taqreerk.Infrastructure.Storage;

/// Google Cloud Storage backend. Stores objects in the configured bucket and
/// generates short-lived V4 signed URLs for reads. Service account credentials
/// are loaded from a JSON file path (local dev) or fall back to Application
/// Default Credentials (Cloud Run). When ADC returns a metadata-server
/// credential (Cloud Run / GCE) it has no private key, so V4 signing is
/// delegated to the IAM Credentials `signBlob` API.
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

        GoogleCredential credential;
        if (!string.IsNullOrWhiteSpace(_settings.GcsCredentialsJsonPath))
        {
            if (!File.Exists(_settings.GcsCredentialsJsonPath))
                throw new InvalidOperationException(
                    $"GCS credentials file not found at: {_settings.GcsCredentialsJsonPath}");

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

        // ServiceAccountCredential carries a private key — sign locally.
        // ComputeCredential (Cloud Run / GCE) has no private key — delegate
        // V4 signing to the IAM Credentials API. The runtime SA must have
        // `roles/iam.serviceAccountTokenCreator` on itself for signBlob to
        // succeed; without that, signed URL requests will throw 403.
        if (credential.UnderlyingCredential is Google.Apis.Auth.OAuth2.ServiceAccountCredential)
        {
            _signer = UrlSigner.FromCredential(credential);
            _logger.LogInformation("GCS UrlSigner: local key (ServiceAccountCredential).");
        }
        else
        {
            var saEmail = ResolveServiceAccountEmail(credential)
                ?? throw new InvalidOperationException(
                    "Could not resolve runtime service-account email for IAM signBlob. " +
                    "Set FileStorage:GcsCredentialsJsonPath, or run on Cloud Run/GCE with the metadata server reachable.");

            var iamClient = IAMCredentialsClient.Create();
            _signer = UrlSigner.FromBlobSigner(new IamBlobSigner(iamClient, saEmail));
            _logger.LogInformation("GCS UrlSigner: IAM signBlob delegate for {ServiceAccount}.", saEmail);
        }
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

    public async Task<StoredFile> UploadPublicAsync(
        Stream content,
        string originalFileName,
        string contentType,
        string folder,
        CancellationToken ct = default)
    {
        // Public uploads land in a SEPARATE bucket from the primary
        // (private) one. This keeps the private bucket — which holds
        // report PDFs, translations, org files — fully PAP-locked, and
        // confines any future misconfiguration of public-read IAM to the
        // public-covers bucket where the worst case is exposing cover
        // images (already meant to be seen on the public web).
        var publicBucket = _settings.GcsPublicBucketName;
        if (string.IsNullOrWhiteSpace(publicBucket))
            throw new InvalidOperationException(
                "FileStorage:GcsPublicBucketName must be set before calling UploadPublicAsync. " +
                "Configure a dedicated public bucket; do not point this at GcsBucketName.");

        // We INTENTIONALLY do not auto-randomise the file name here — the
        // cover-variant pipeline relies on stable, predictable keys like
        // `public/covers/{reportId}/thumb.webp`, so callers pass the exact
        // filename they want. Folders still get the bucket prefix applied
        // so prod/staging deploys sharing the same public bucket don't
        // collide on coverIds.
        var safeFolder = SanitizeFolder(folder);
        var prefix = SanitizeFolder(_settings.GcsBucketPrefix);
        var withPrefix = string.IsNullOrEmpty(prefix) ? safeFolder
            : string.IsNullOrEmpty(safeFolder) ? prefix
            : $"{prefix}/{safeFolder}";
        var objectKey = string.IsNullOrEmpty(withPrefix)
            ? originalFileName
            : $"{withPrefix}/{originalFileName}";

        // The object payload carries the metadata GCS uses to set response
        // headers on every subsequent GET: Cache-Control turns the browser
        // into a 1-year edge cache for variants. `immutable` tells the
        // browser it never needs to revalidate — safe because variant
        // filenames already encode size, and a re-upload writes new keys
        // under a new coverId folder.
        var obj = new Google.Apis.Storage.v1.Data.Object
        {
            Bucket = publicBucket,
            Name = objectKey,
            ContentType = contentType,
            CacheControl = "public, max-age=31536000, immutable",
        };
        // No per-object ACL: the public bucket is configured with a
        // bucket-level `allUsers:objectViewer` IAM binding (see PLAN.md
        // Phase 6 operator runbook). UBLA on the public bucket means ACL
        // writes are forbidden anyway — bucket IAM is the only path.
        var uploaded = await _storage.UploadObjectAsync(
            obj,
            source: content,
            options: null,
            cancellationToken: ct);

        var size = uploaded.Size.HasValue ? (long)uploaded.Size.Value : 0L;
        _logger.LogInformation(
            "Uploaded PUBLIC gs://{Bucket}/{Key} ({Size} bytes, cache=1y)",
            publicBucket, objectKey, size);
        return new StoredFile(objectKey, size, contentType);
    }

    public string GetPublicUrl(string objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
            throw new ArgumentException("Object key is required.", nameof(objectKey));
        var publicBucket = _settings.GcsPublicBucketName;
        if (string.IsNullOrWhiteSpace(publicBucket))
            throw new InvalidOperationException(
                "FileStorage:GcsPublicBucketName must be set to resolve a public URL.");
        // storage.googleapis.com is the canonical public host; no signing,
        // no token. Object key is already URL-safe (we control the alphabet
        // in SanitizeFolder + the variant naming convention).
        return $"https://storage.googleapis.com/{publicBucket}/{objectKey}";
    }

    public async Task<string> GetReadUrlAsync(string objectKey, TimeSpan? lifetime = null, CancellationToken ct = default)
    {
        var window = lifetime ?? TimeSpan.FromMinutes(_settings.SignedUrlMinutes);

        // Bake response-content-type + response-content-disposition into the
        // signed URL itself. GCS V4 signs ALL query parameters at signing time,
        // so these have to be present in the RequestTemplate — appending them
        // afterwards on the frontend triggers SignatureDoesNotMatch.
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

    /// On Cloud Run / GCE the metadata server exposes the SA email at
    /// /computeMetadata/v1/instance/service-accounts/default/email. We hit it
    /// once at startup; if unreachable (e.g. the deployment doesn't run on
    /// GCP) we surface the failure rather than silently falling back.
    private static string? ResolveServiceAccountEmail(GoogleCredential credential)
    {
        // ComputeCredential doesn't expose the email directly, so we ask the
        // metadata server. We deliberately use a short timeout — if we're not
        // on GCP this should fail fast, not block startup.
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            http.DefaultRequestHeaders.Add("Metadata-Flavor", "Google");
            var resp = http.GetStringAsync(
                "http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/email")
                .GetAwaiter().GetResult();
            return string.IsNullOrWhiteSpace(resp) ? null : resp.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// Implements UrlSigner's IBlobSigner by calling IAM Credentials
    /// `projects/-/serviceAccounts/{email}:signBlob`. The runtime SA must
    /// have `roles/iam.serviceAccountTokenCreator` on itself.
    private sealed class IamBlobSigner : UrlSigner.IBlobSigner
    {
        private readonly IAMCredentialsClient _client;
        private readonly string _serviceAccountEmail;
        private readonly string _resourceName;

        public IamBlobSigner(IAMCredentialsClient client, string serviceAccountEmail)
        {
            _client = client;
            _serviceAccountEmail = serviceAccountEmail;
            _resourceName = $"projects/-/serviceAccounts/{serviceAccountEmail}";
        }

        public string Id => _serviceAccountEmail;

        // GCS V4 signed URLs use RSA-SHA256. IAM signBlob signs with the SA's
        // managed RSA key, which matches.
        public string Algorithm => "GOOG4-RSA-SHA256";

        public string CreateSignature(byte[] data, UrlSigner.BlobSignerParameters parameters)
        {
            var resp = _client.SignBlob(new SignBlobRequest
            {
                Name = _resourceName,
                Payload = ByteString.CopyFrom(data),
            });
            // UrlSigner expects base64; it base64-decodes then hex-encodes itself.
            return Convert.ToBase64String(resp.SignedBlob.ToByteArray());
        }

        public async Task<string> CreateSignatureAsync(
            byte[] data,
            UrlSigner.BlobSignerParameters parameters,
            CancellationToken cancellationToken)
        {
            var resp = await _client.SignBlobAsync(new SignBlobRequest
            {
                Name = _resourceName,
                Payload = ByteString.CopyFrom(data),
            }, cancellationToken).ConfigureAwait(false);
            return Convert.ToBase64String(resp.SignedBlob.ToByteArray());
        }
    }
}
