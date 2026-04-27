using Microsoft.Extensions.Options;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Settings;

namespace Taqreerk.Infrastructure.Storage;

/// Stores files on the local filesystem under {ContentRootPath}/{LocalRoot}.
/// Useful for dev and tests; not suitable for clustered/containerized prod
/// where the filesystem isn't shared across instances.
public class LocalFileStorage : IFileStorage
{
    private readonly FileStorageSettings _settings;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<LocalFileStorage> _logger;

    public LocalFileStorage(
        IOptions<FileStorageSettings> settings,
        IWebHostEnvironment env,
        ILogger<LocalFileStorage> logger)
    {
        _settings = settings.Value;
        _env = env;
        _logger = logger;
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
        // Use a GUID + original extension for the on-disk name; original name is
        // preserved by callers in the entity if they need it for display.
        var fileName = $"{Guid.NewGuid():N}{extension}";
        var objectKey = string.IsNullOrEmpty(safeFolder) ? fileName : $"{safeFolder}/{fileName}";

        var rootDir = ResolveRoot();
        var folderPath = Path.Combine(rootDir, safeFolder);
        Directory.CreateDirectory(folderPath);

        var fullPath = Path.Combine(folderPath, fileName);
        await using (var fs = File.Create(fullPath))
        {
            await content.CopyToAsync(fs, ct);
        }

        var size = new FileInfo(fullPath).Length;
        _logger.LogInformation("Stored {ObjectKey} ({Size} bytes) at {Path}", objectKey, size, fullPath);

        return new StoredFile(objectKey, size, contentType);
    }

    public Task<string> GetReadUrlAsync(string objectKey, TimeSpan? lifetime = null, CancellationToken ct = default)
    {
        // Local storage is "public" via the static-files middleware mounted at LocalPublicBaseUrl.
        // Lifetime is ignored — local URLs don't expire.
        var baseUrl = _settings.LocalPublicBaseUrl.TrimEnd('/');
        return Task.FromResult($"{baseUrl}/{objectKey}");
    }

    public Task DeleteAsync(string objectKey, CancellationToken ct = default)
    {
        var rootDir = ResolveRoot();
        var fullPath = Path.Combine(rootDir, objectKey.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            _logger.LogInformation("Deleted {ObjectKey}", objectKey);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListAsync(string prefix, int max = 100, CancellationToken ct = default)
    {
        var rootDir = ResolveRoot();
        var prefixPath = Path.Combine(rootDir, prefix.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(prefixPath))
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        var rootFull = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar);
        var keys = Directory
            .EnumerateFiles(prefixPath, "*", SearchOption.AllDirectories)
            .Take(max)
            .Select(p => Path.GetFullPath(p)[(rootFull.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/'))
            .ToList();
        return Task.FromResult<IReadOnlyList<string>>(keys);
    }

    private string ResolveRoot()
    {
        var root = _settings.LocalRoot;
        return Path.IsPathRooted(root) ? root : Path.Combine(_env.ContentRootPath, root);
    }

    private static string SanitizeFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return string.Empty;
        // Strip path traversal; allow letters, digits, dash, underscore, slash.
        var cleaned = new string(folder
            .Trim('/', '\\')
            .Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '/' ? c : '-')
            .ToArray());
        return cleaned;
    }
}
