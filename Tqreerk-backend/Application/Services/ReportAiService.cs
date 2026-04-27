using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Taqreerk.Application.DTOs.Reports;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Settings;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

/// AI processing orchestrator. Pipeline:
///   1. Ingestion         → ai-service /reports/ingest (writes report_pages,
///                          updates Report.PageCount).
///   2. Summarization     → ai-service /reports/summarize (summary + findings +
///                          topics, persisted in ReportAiContent[lang=ar]).
///   3. Translation (en)  → ai-service /reports/translate (returns gs:// URL,
///                          persisted in ReportTranslation[lang=en]).
///   4. Publish           → Report.Status = Published.
///
/// Each step is its own AiJob row so we can retry individual stages on failure.
public class ReportAiService : IReportAiService
{
    private const string DefaultTargetTranslationLanguage = "en";

    private readonly TaqreerkDbContext _db;
    private readonly IAiServiceClient _ai;
    private readonly IFileStorage _files;
    private readonly FileStorageSettings _storage;
    private readonly AiServiceSettings _settings;
    private readonly ILogger<ReportAiService> _logger;

    public ReportAiService(
        TaqreerkDbContext db,
        IAiServiceClient ai,
        IFileStorage files,
        IOptions<FileStorageSettings> storage,
        IOptions<AiServiceSettings> settings,
        ILogger<ReportAiService> logger)
    {
        _db = db;
        _ai = ai;
        _files = files;
        _storage = storage.Value;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task EnqueueIngestAsync(Guid reportId, CancellationToken ct = default)
    {
        var report = await _db.Reports.FirstOrDefaultAsync(r => r.Id == reportId, ct)
            ?? throw new KeyNotFoundException("Report not found.");
        if (string.IsNullOrWhiteSpace(report.FileUrl))
            throw new InvalidOperationException("Report has no file URL — cannot ingest.");

        // Idempotent: only create a new ingest job if there isn't an active one.
        var hasActive = await _db.AiJobs.AnyAsync(
            j => j.ReportId == reportId
              && j.JobType == AiJobType.Ingestion
              && (j.Status == AiJobStatus.Pending || j.Status == AiJobStatus.Processing),
            ct);
        if (hasActive) return;

        _db.AiJobs.Add(new AiJob
        {
            ReportId = reportId,
            OrganizationId = report.OrganizationId,
            JobType = AiJobType.Ingestion,
            Status = AiJobStatus.Pending,
        });
        report.Status = ReportStatus.PendingReview; // "Processing" — re-using the existing enum for now.
        await _db.SaveChangesAsync(ct);
    }

    public async Task RegenerateAsync(Guid currentUserId, Guid reportId, CancellationToken ct = default)
    {
        await AssertCallerOwnsReportAsync(currentUserId, reportId, ct);

        // Reset Failed jobs back to Pending. If everything completed already,
        // create a fresh ingest job instead so the user can re-run the whole
        // pipeline (e.g. after the file changed).
        var jobs = await _db.AiJobs
            .Where(j => j.ReportId == reportId)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(ct);

        var anyResetable = jobs.Any(j => j.Status == AiJobStatus.Failed);
        if (anyResetable)
        {
            foreach (var j in jobs.Where(j => j.Status == AiJobStatus.Failed))
            {
                j.Status = AiJobStatus.Pending;
                j.ErrorMessage = null;
                j.StartedAt = null;
                j.CompletedAt = null;
            }
            await _db.SaveChangesAsync(ct);
        }
        else
        {
            await EnqueueIngestAsync(reportId, ct);
        }
    }

    public async Task<ReportAiStatusDto> GetStatusAsync(Guid currentUserId, Guid reportId, CancellationToken ct = default)
    {
        await AssertCallerOwnsReportAsync(currentUserId, reportId, ct);

        var jobs = await _db.AiJobs
            .AsNoTracking()
            .Where(j => j.ReportId == reportId)
            .OrderBy(j => j.CreatedAt)
            .Select(j => new AiJobStatusDto(
                j.Id, j.JobType.ToString(), j.Status.ToString(),
                j.ErrorMessage, j.StartedAt, j.CompletedAt))
            .ToListAsync(ct);

        var contents = await _db.ReportAiContents
            .AsNoTracking()
            .Where(c => c.ReportId == reportId)
            .ToListAsync(ct);

        // Pull raw rows first; the TranslatedFileUrl is stored as a gs:// URI
        // (that's what the AI service hands back). Browsers can't open gs://,
        // so we resolve each one to a short-lived signed HTTPS URL before
        // shaping the DTO. URIs that don't belong to our bucket pass through
        // unchanged so we don't accidentally break external links.
        var translationRows = await _db.ReportTranslations
            .AsNoTracking()
            .Where(t => t.ReportId == reportId)
            .Select(t => new { t.Language, t.TranslationStatus, t.TranslatedFileUrl, t.TranslatedAt })
            .ToListAsync(ct);

        var translations = new List<TranslationStatusDto>(translationRows.Count);
        foreach (var t in translationRows)
        {
            var resolvedUrl = await ResolveTranslatedFileUrlAsync(t.TranslatedFileUrl, ct);
            translations.Add(new TranslationStatusDto(
                t.Language, t.TranslationStatus.ToString(), resolvedUrl, t.TranslatedAt));
        }

        var ar = contents.FirstOrDefault(c => c.Language == "ar");
        var en = contents.FirstOrDefault(c => c.Language == "en");

        var overall = ComputeOverallStatus(jobs, contents.Count > 0);

        return new ReportAiStatusDto(
            reportId,
            overall,
            jobs,
            ar is null ? null : ToContentDto(ar),
            en is null ? null : ToContentDto(en),
            translations
        );
    }

    public async Task ProcessJobAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await _db.AiJobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
        {
            _logger.LogWarning("AiJob {JobId} not found — skipping", jobId);
            return;
        }
        if (job.Status != AiJobStatus.Pending)
        {
            _logger.LogDebug("AiJob {JobId} already in status {Status} — skipping", jobId, job.Status);
            return;
        }
        if (!job.ReportId.HasValue)
        {
            await MarkFailedAsync(job, "AiJob has no ReportId", ct);
            return;
        }

        job.Status = AiJobStatus.Processing;
        job.StartedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        try
        {
            switch (job.JobType)
            {
                case AiJobType.Ingestion:
                    await ProcessIngestAsync(job, ct);
                    break;
                case AiJobType.Summarization:
                    await ProcessSummarizeAsync(job, ct);
                    break;
                case AiJobType.Translation:
                    await ProcessTranslateAsync(job, ct);
                    break;
                default:
                    await MarkFailedAsync(job, $"Unsupported job type: {job.JobType}", ct);
                    return;
            }

            job.Status = AiJobStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AiJob {JobId} ({Type}) failed", job.Id, job.JobType);
            await MarkFailedAsync(job, ex.Message, ct);
        }
    }

    // ── pipeline steps ───────────────────────────────────────────────────────

    private async Task ProcessIngestAsync(AiJob job, CancellationToken ct)
    {
        var report = await LoadReportAsync(job, ct);
        var gsUri = ToGcsUri(report.FileUrl!);

        var result = await _ai.IngestAsync(report.Id, gsUri, ct);

        report.PageCount = result.PagesProcessed;
        job.OutputData = JsonSerializer.Serialize(new { pages_processed = result.PagesProcessed });

        // Chain: Summarize next.
        EnqueueChild(report, AiJobType.Summarization);
    }

    private async Task ProcessSummarizeAsync(AiJob job, CancellationToken ct)
    {
        var report = await LoadReportAsync(job, ct);

        // Idempotent: skip the AI call if we already have an Arabic summary.
        var existing = await _db.ReportAiContents
            .FirstOrDefaultAsync(c => c.ReportId == report.Id && c.Language == report.OriginalLanguage, ct);
        if (existing is not null && !string.IsNullOrEmpty(existing.Summary))
        {
            EnqueueChild(report, AiJobType.Translation);
            return;
        }

        var result = await _ai.SummarizeAsync(report.Id, ct);

        var content = existing ?? new ReportAiContent
        {
            ReportId = report.Id,
            Language = report.OriginalLanguage,
            AiJobId = job.Id,
        };
        content.Summary = result.Summary;
        content.KeyFindings = JsonSerializer.Serialize(result.KeyFindings);
        content.Indicators = JsonSerializer.Serialize(result.Topics); // re-using the indicators jsonb column for topics
        content.GeneratedAt = DateTime.UtcNow;
        content.AiJobId = job.Id;

        if (existing is null) _db.ReportAiContents.Add(content);

        job.OutputData = JsonSerializer.Serialize(new
        {
            summary_chars = result.Summary.Length,
            key_findings = result.KeyFindings.Count,
            topics = result.Topics.Count,
        });

        // Chain: Translate (default to English; if the source IS English we'll
        // translate to Arabic instead — same logic, opposite direction).
        EnqueueChild(report, AiJobType.Translation);
    }

    private async Task ProcessTranslateAsync(AiJob job, CancellationToken ct)
    {
        var report = await LoadReportAsync(job, ct);

        var sourceLang = string.IsNullOrWhiteSpace(report.OriginalLanguage) ? "ar" : report.OriginalLanguage;
        var targetLang = sourceLang == "ar" ? DefaultTargetTranslationLanguage : "ar";

        // Idempotent: if a translation row already has a file URL, skip.
        var existing = await _db.ReportTranslations
            .FirstOrDefaultAsync(t => t.ReportId == report.Id && t.Language == targetLang, ct);
        if (existing is not null && !string.IsNullOrWhiteSpace(existing.TranslatedFileUrl))
        {
            await PublishAsync(report);
            return;
        }

        // Track in-flight state on the translation row from the start so the
        // status endpoint can show "translating" rather than nothing at all.
        if (existing is null)
        {
            existing = new ReportTranslation
            {
                ReportId = report.Id,
                Language = targetLang,
                TranslationStatus = TranslationStatus.Processing,
                AiJobId = job.Id,
            };
            _db.ReportTranslations.Add(existing);
        }
        else
        {
            existing.TranslationStatus = TranslationStatus.Processing;
            existing.AiJobId = job.Id;
        }
        await _db.SaveChangesAsync(ct);

        var gsUri = ToGcsUri(report.FileUrl!);
        var outputPrefix = BuildTranslationOutputPrefix(report.Id, targetLang);

        TranslateResult result;
        try
        {
            result = await _ai.TranslateAsync(report.Id, gsUri, outputPrefix, targetLang, sourceLang, ct);
        }
        catch
        {
            existing.TranslationStatus = TranslationStatus.Failed;
            await _db.SaveChangesAsync(ct);
            throw;
        }

        existing.TranslatedFileUrl = result.TranslatedFileUrl;
        existing.TranslationStatus = TranslationStatus.Completed;
        existing.TranslatedAt = DateTime.UtcNow;

        job.OutputData = JsonSerializer.Serialize(new
        {
            translated_file_url = result.TranslatedFileUrl,
            target = targetLang,
        });

        await PublishAsync(report);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<Report> LoadReportAsync(AiJob job, CancellationToken ct)
    {
        var report = await _db.Reports.FirstOrDefaultAsync(r => r.Id == job.ReportId!.Value, ct)
            ?? throw new InvalidOperationException($"Report {job.ReportId} not found for job {job.Id}");
        if (string.IsNullOrWhiteSpace(report.FileUrl))
            throw new InvalidOperationException($"Report {report.Id} has no file URL");
        return report;
    }

    private void EnqueueChild(Report report, AiJobType nextType)
    {
        _db.AiJobs.Add(new AiJob
        {
            ReportId = report.Id,
            OrganizationId = report.OrganizationId,
            JobType = nextType,
            Status = AiJobStatus.Pending,
        });
    }

    private Task PublishAsync(Report report)
    {
        report.Status = ReportStatus.Published;
        return Task.CompletedTask;
    }

    private async Task MarkFailedAsync(AiJob job, string error, CancellationToken ct)
    {
        job.Status = AiJobStatus.Failed;
        job.ErrorMessage = error.Length > 4000 ? error[..4000] : error;
        job.CompletedAt = DateTime.UtcNow;
        try { await _db.SaveChangesAsync(ct); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist Failed state for AiJob {JobId}", job.Id);
        }
    }

    private async Task AssertCallerOwnsReportAsync(Guid userId, Guid reportId, CancellationToken ct)
    {
        var orgId = await _db.OrganizationMembers
            .Where(m => m.UserId == userId && m.IsActive)
            .Select(m => (Guid?)m.OrganizationId)
            .FirstOrDefaultAsync(ct)
            ?? throw new UnauthorizedAccessException("Caller is not a member of any organization.");

        var ownerOrg = await _db.Reports
            .Where(r => r.Id == reportId)
            .Select(r => (Guid?)r.OrganizationId)
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Report not found.");

        if (orgId != ownerOrg)
            throw new UnauthorizedAccessException("This report belongs to another organization.");
    }

    /// Convert a stored object key (e.g. "taqreerk-uploads-dev/reports/abc/xyz.pdf")
    /// into the gs:// URI the ai-service expects.
    private string ToGcsUri(string objectKey)
    {
        if (objectKey.StartsWith("gs://", StringComparison.OrdinalIgnoreCase))
            return objectKey;
        if (string.IsNullOrWhiteSpace(_storage.GcsBucketName))
            throw new InvalidOperationException("GcsBucketName is not configured — cannot construct gs:// URI for ai-service.");
        return $"gs://{_storage.GcsBucketName}/{objectKey.TrimStart('/')}";
    }

    private string BuildTranslationOutputPrefix(Guid reportId, string targetLang)
    {
        var basePrefix = string.IsNullOrWhiteSpace(_settings.TranslationOutputPrefix)
            ? $"gs://{_storage.GcsBucketName}/{_storage.GcsBucketPrefix}/translations".TrimEnd('/')
            : _settings.TranslationOutputPrefix.TrimEnd('/');
        return $"{basePrefix}/{reportId}/{targetLang}/";
    }

    /// The AI service stores the translated PDF under a gs:// URI which a
    /// browser cannot open. For URIs in our own bucket, hand back a short-lived
    /// V4 signed HTTPS URL so the user can click and download. Anything else
    /// (different bucket, already https://, null/empty) passes through.
    ///
    /// Workaround: Cloud Translation v3 mangles output filenames (the AI service
    /// reports `…/{guid}.pdf` but actually writes `…/{long_slug}_translations.pdf`).
    /// If signing the reported key returns NoSuchKey on download, we fall back
    /// to listing the parent prefix and using the first PDF we find. The DB
    /// keeps the gs:// URI as-is so we can compare with what the AI service
    /// reports if this ever stops being a known bug.
    private async Task<string?> ResolveTranslatedFileUrlAsync(string? uri, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        if (!uri.StartsWith("gs://", StringComparison.OrdinalIgnoreCase)) return uri;

        // Parse "gs://bucket/object/key" → ("bucket", "object/key").
        var rest = uri.Substring("gs://".Length);
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1) return uri;
        var bucket = rest[..slash];
        var objectKey = rest[(slash + 1)..];

        // Different bucket → not ours to sign for. Return as-is.
        if (!string.Equals(bucket, _storage.GcsBucketName, StringComparison.OrdinalIgnoreCase))
            return uri;

        try
        {
            // Best case: the AI service told us the truth. List the parent prefix
            // and check whether the exact reported key is there.
            var lastSlash = objectKey.LastIndexOf('/');
            if (lastSlash <= 0) return await _files.GetReadUrlAsync(objectKey, ct: ct);

            var parentPrefix = objectKey[..(lastSlash + 1)];
            var keysAtPrefix = await _files.ListAsync(parentPrefix, max: 25, ct: ct);

            if (keysAtPrefix.Contains(objectKey, StringComparer.Ordinal))
                return await _files.GetReadUrlAsync(objectKey, ct: ct);

            // The reported key isn't there. Pick the first PDF in the same
            // folder. Cloud Translation v3 writes one file per translate call
            // so this is unambiguous in practice.
            var actual = keysAtPrefix.FirstOrDefault(
                k => k.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

            if (actual is null)
            {
                _logger.LogWarning(
                    "Translation reported {Reported} but no PDF found at prefix {Prefix}",
                    uri, parentPrefix);
                return uri;
            }

            _logger.LogInformation(
                "Translation reported {Reported}, actual file at {Actual} — signing actual",
                objectKey, actual);
            return await _files.GetReadUrlAsync(actual, ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sign translation URL for {Uri}", uri);
            return uri;
        }
    }

    private static ReportAiContentDto ToContentDto(ReportAiContent c)
    {
        var keyFindings = ParseJsonStringArray(c.KeyFindings);
        var topics = ParseJsonStringArray(c.Indicators);
        return new ReportAiContentDto(c.Language, c.Summary, keyFindings, topics, c.GeneratedAt);
    }

    private static IReadOnlyList<string> ParseJsonStringArray(string? jsonb)
    {
        if (string.IsNullOrWhiteSpace(jsonb)) return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(jsonb) ?? new List<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string ComputeOverallStatus(IReadOnlyList<AiJobStatusDto> jobs, bool hasContent)
    {
        if (jobs.Count == 0) return "NotStarted";
        if (jobs.Any(j => j.Status == nameof(AiJobStatus.Failed))) return "Failed";
        if (jobs.Any(j => j.Status == nameof(AiJobStatus.Pending) || j.Status == nameof(AiJobStatus.Processing)))
            return "Processing";
        return hasContent ? "Completed" : "Completed";
    }
}
