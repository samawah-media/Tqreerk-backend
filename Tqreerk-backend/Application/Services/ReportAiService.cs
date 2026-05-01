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

/// AI processing orchestrator (queue-only). The AI service's worker drains the
/// shared ai_jobs table, so this service's role is to:
///
///   1. Approve            → admin clicks Approve in ReviewService → we
///                           EnqueueIngestAsync (creates one Ingestion row
///                           with step=ingest+summarize). The AI worker runs
///                           extraction + summarization in one pass and writes
///                           into report_chunks and report_ai_contents[ar].
///   2. (manual) Translate → org user clicks the "ترجم التقرير" button →
///                           EnqueueTranslateAsync. Gated by the org's
///                           TranslationEnabled flag (toggled by staff).
///   3. Reconcile          → FinalizeCompletedJobsAsync flips reports out of
///                           ProcessingAi and syncs ReportTranslation rows
///                           after the AI worker terminates each job.
public class ReportAiService : IReportAiService
{
    private const string DefaultTargetTranslationLanguage = "en";

    private readonly TaqreerkDbContext _db;
    private readonly IAiServiceClient _ai;
    private readonly IFileStorage _files;
    private readonly FileStorageSettings _storage;
    private readonly AiServiceSettings _settings;
    private readonly IQuotaService _quota;
    private readonly ILogger<ReportAiService> _logger;

    // We POST directly to the AI service's HTTP endpoints (/reports/ingest,
    // /reports/translate). The previous queue-only design (insert ai_jobs row
    // and let the AI worker pick it up) doesn't survive a serverless deploy:
    // Cloud Run scales the AI service to zero when idle, so the worker poll
    // loop only runs while a container exists. Hitting the HTTP endpoint
    // both wakes a container AND lets the AI side own the row insertion
    // exactly the way its dispatch expects.
    public ReportAiService(
        TaqreerkDbContext db,
        IAiServiceClient ai,
        IFileStorage files,
        IOptions<FileStorageSettings> storage,
        IOptions<AiServiceSettings> settings,
        IQuotaService quota,
        ILogger<ReportAiService> logger)
    {
        _db = db;
        _ai = ai;
        _files = files;
        _storage = storage.Value;
        _settings = settings.Value;
        _quota = quota;
        _logger = logger;
    }

    public async Task EnqueueIngestAsync(Guid reportId, CancellationToken ct = default)
    {
        _logger.LogInformation("[ai] EnqueueIngest report={ReportId}", reportId);
        var report = await _db.Reports.FirstOrDefaultAsync(r => r.Id == reportId, ct)
            ?? throw new KeyNotFoundException("Report not found.");
        if (string.IsNullOrWhiteSpace(report.FileUrl))
            throw new InvalidOperationException("Report has no file URL — cannot ingest.");

        // Idempotent: skip if an Ingestion is already Pending/Processing.
        // We check the AI-service-owned ai_jobs table here because that's
        // where /reports/ingest writes its row.
        var hasActive = await _db.AiJobs.AnyAsync(
            j => j.ReportId == reportId
              && j.JobType == AiJobType.Ingestion
              && (j.Status == AiJobStatus.Pending || j.Status == AiJobStatus.Processing),
            ct);
        if (hasActive)
        {
            _logger.LogInformation("[ai] EnqueueIngest report={ReportId} skipped (active job exists)", reportId);
            return;
        }

        // Per-org daily ingest cap — runs AFTER the idempotency check so a
        // duplicate request doesn't burn a quota slot. Throws
        // QuotaExceededException; controller layer maps it to HTTP 429.
        await _quota.AssertUnderJobQuotaAsync(report.OrganizationId, AiJobType.Ingestion, ct);

        // POST to the AI service. The endpoint is fire-and-forget on the
        // server side (returns 202 with a job_id and runs ingest+summarize
        // in a background asyncio task). Calling it via HTTP also wakes the
        // Cloud Run container so the AI worker is actually alive to drive
        // the row to terminal state.
        var gsUri = ToGcsUri(report.FileUrl);
        try
        {
            var enqueued = await _ai.IngestAsync(reportId, gsUri, ct);
            _logger.LogInformation(
                "[ai] EnqueueIngest report={ReportId} accepted by ai-service job={AiJobId}",
                reportId, enqueued.JobId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[ai] EnqueueIngest report={ReportId} failed to call ai-service",
                reportId);
            throw;
        }

        // Flip the report into ProcessingAi locally so the dashboard reflects
        // the in-flight state immediately. The status finalizer will move it
        // forward to Published when the ai_jobs row reaches Completed.
        report.Status = ReportStatus.ProcessingAi;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Manually trigger a translation for the report. Gated by the org's
    /// TranslationEnabled flag and by ingest having produced an Arabic
    /// summary. Calls the AI service's synchronous /reports/translate
    /// endpoint — the call itself runs Cloud Translation v3 server-side
    /// and returns once the translated PDF is in GCS.
    /// </summary>
    public async Task EnqueueTranslateAsync(Guid currentUserId, Guid reportId, CancellationToken ct = default)
    {
        await AssertCallerOwnsReportAsync(currentUserId, reportId, ct);

        var report = await _db.Reports
            .Include(r => r.Organization)
            .FirstOrDefaultAsync(r => r.Id == reportId, ct)
            ?? throw new KeyNotFoundException("Report not found.");

        if (string.IsNullOrWhiteSpace(report.FileUrl))
            throw new InvalidOperationException("Report has no file URL — cannot translate.");

        // Per-org gate. Toggled by staff from the admin app's Organizations page.
        if (report.Organization is null || !report.Organization.TranslationEnabled)
            throw new UnauthorizedAccessException("Translation is not enabled for this organization.");

        // Translation requires the AR summary (and hence ingest) to be done.
        var hasArContent = await _db.ReportAiContents
            .AnyAsync(c => c.ReportId == reportId && c.Language == "ar", ct);
        if (!hasArContent)
            throw new InvalidOperationException("Translation requires the report to be ingested first.");

        // Idempotent: skip if there's already an active or completed translation.
        var hasActive = await _db.AiJobs.AnyAsync(
            j => j.ReportId == reportId
              && j.JobType == AiJobType.Translation
              && (j.Status == AiJobStatus.Pending || j.Status == AiJobStatus.Processing),
            ct);
        if (hasActive) return;

        var sourceLang = string.IsNullOrWhiteSpace(report.OriginalLanguage) ? "ar" : report.OriginalLanguage;
        var targetLang = sourceLang == "ar" ? DefaultTargetTranslationLanguage : "ar";

        var existing = await _db.ReportTranslations
            .FirstOrDefaultAsync(t => t.ReportId == reportId && t.Language == targetLang, ct);
        if (existing is not null && !string.IsNullOrWhiteSpace(existing.TranslatedFileUrl)) return;

        await _quota.AssertUnderJobQuotaAsync(report.OrganizationId, AiJobType.Translation, ct);

        var gsUri = ToGcsUri(report.FileUrl);
        var outputPrefix = BuildTranslationOutputPrefix(report.Id, targetLang);

        // Mark Processing up-front so the user UI flips to "translating…"
        // immediately. The HTTP call to /translate is synchronous and can take
        // 1–3 min for Cloud Translation v3 — we can't keep the user staring
        // at a spinner with no state change.
        if (existing is null)
        {
            existing = new ReportTranslation
            {
                ReportId = reportId,
                Language = targetLang,
                TranslationStatus = TranslationStatus.Processing,
            };
            _db.ReportTranslations.Add(existing);
        }
        else
        {
            existing.TranslationStatus = TranslationStatus.Processing;
        }
        await _db.SaveChangesAsync(ct);

        TranslateResult result;
        try
        {
            result = await _ai.TranslateAsync(reportId, gsUri, outputPrefix, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[ai] EnqueueTranslate report={ReportId} ai-service call failed",
                reportId);

            // Mark the translation row Failed so the user UI can show the error
            // state instead of leaving it Processing forever.
            existing.TranslationStatus = TranslationStatus.Failed;
            await _db.SaveChangesAsync(ct);
            throw;
        }

        var detectedTarget = string.IsNullOrWhiteSpace(result.TargetLanguage) ? targetLang : result.TargetLanguage;
        existing.Language = detectedTarget;
        existing.TranslatedFileUrl = result.TranslatedFileUrl;
        existing.TranslationStatus = TranslationStatus.Completed;
        existing.TranslatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "[ai] EnqueueTranslate report={ReportId} target={Target} (Pending)",
            reportId, targetLang);
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

        // Surfacing the org-level flag here lets the user-side report page
        // show/hide the manual "Translate" button without a second round-trip
        // to the organization endpoint.
        var translationEnabled = await _db.Reports
            .AsNoTracking()
            .Where(r => r.Id == reportId)
            .Select(r => r.Organization!.TranslationEnabled)
            .FirstOrDefaultAsync(ct);

        return new ReportAiStatusDto(
            reportId,
            overall,
            jobs,
            ar is null ? null : ToContentDto(ar),
            en is null ? null : ToContentDto(en),
            translations,
            translationEnabled
        );
    }

    /// <summary>
    /// Reconciles .NET-owned report state with what the AI service has finished.
    /// The AI worker drives every ai_jobs row to terminal status (Completed /
    /// Failed) and writes content into report_chunks / report_ai_contents /
    /// report_translations. It does NOT update reports.Status, because the
    /// .NET side owns that lifecycle. This method bridges the gap by moving
    /// reports out of ProcessingAi once their Ingestion job succeeds, and
    /// updating in-flight ReportTranslation rows when their matching
    /// Translation ai_job finishes.
    /// </summary>
    public async Task FinalizeCompletedJobsAsync(CancellationToken ct = default)
    {
        // Look at terminally-resolved jobs from the last hour. We can't rely on
        // a "processed" flag here without a schema change, so we re-scan and
        // make every transition idempotent.
        var since = DateTime.UtcNow.AddHours(-1);
        var recent = await _db.AiJobs
            .Where(j => j.CompletedAt != null
                     && j.CompletedAt >= since
                     && (j.Status == AiJobStatus.Completed || j.Status == AiJobStatus.Failed)
                     && j.ReportId != null)
            .Select(j => new { j.Id, j.ReportId, j.JobType, j.Status, j.OutputData })
            .ToListAsync(ct);

        if (recent.Count == 0) return;

        // Group by report so we make one decision per report regardless of
        // how many jobs flipped at once.
        var byReport = recent.GroupBy(j => j.ReportId!.Value);
        foreach (var grp in byReport)
        {
            ct.ThrowIfCancellationRequested();
            var reportId = grp.Key;
            var report = await _db.Reports.FirstOrDefaultAsync(r => r.Id == reportId, ct);
            if (report is null) continue;

            // Ingestion outcome — flips the report out of ProcessingAi and
            // (when the AI worker ran the combined ingest+summarize step) copies
            // the summary into report_ai_contents so the user-facing UI can find
            // it. The AI service stores its results only in ai_jobs.OutputData;
            // this side keeps the canonical location for read-side queries.
            var ingest = grp.FirstOrDefault(j => j.JobType == AiJobType.Ingestion);
            if (ingest is not null)
            {
                if (ingest.Status == AiJobStatus.Completed)
                {
                    var pages = TryReadIntFromJson(ingest.OutputData, "pages_processed");
                    if (pages is not null) report.PageCount = pages.Value;
                    if (report.Status == ReportStatus.ProcessingAi)
                        report.Status = ReportStatus.Published;

                    await CopySummaryToContentAsync(report, ingest.Id, ingest.OutputData, ct);
                }
                else if (ingest.Status == AiJobStatus.Failed && report.Status == ReportStatus.ProcessingAi)
                {
                    // Park the report back at Approved so staff can retry the
                    // pipeline manually instead of leaving it stuck mid-flight.
                    report.Status = ReportStatus.Approved;
                }
            }

            // Translation outcome — sync the corresponding ReportTranslation
            // row so the UI shows the right state and download link.
            var translate = grp.FirstOrDefault(j => j.JobType == AiJobType.Translation);
            if (translate is not null)
            {
                var translatedUrl = TryReadStringFromJson(translate.OutputData, "translated_file_url");
                var targetLang = TryReadStringFromJson(translate.OutputData, "target_language")
                              ?? TryReadStringFromJson(translate.OutputData, "target")
                              ?? (report.OriginalLanguage == "en" ? "ar" : "en");

                var row = await _db.ReportTranslations
                    .FirstOrDefaultAsync(t => t.ReportId == reportId && t.Language == targetLang, ct);
                if (row is null)
                {
                    row = new ReportTranslation
                    {
                        ReportId = reportId,
                        Language = targetLang,
                    };
                    _db.ReportTranslations.Add(row);
                }

                if (translate.Status == AiJobStatus.Completed && !string.IsNullOrWhiteSpace(translatedUrl))
                {
                    row.TranslatedFileUrl = translatedUrl;
                    row.TranslationStatus = TranslationStatus.Completed;
                    row.TranslatedAt ??= DateTime.UtcNow;
                }
                else if (translate.Status == AiJobStatus.Failed)
                {
                    row.TranslationStatus = TranslationStatus.Failed;
                }
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// When the AI worker runs the combined ingest+summarize step, the summary
    /// (+ key findings + topics) lands in `ai_jobs.OutputData` only. The .NET
    /// read path expects them in report_ai_contents (the canonical place that
    /// GetStatusAsync returns to the UI), so copy them across once the job
    /// is Completed. Idempotent — re-running on the same row updates existing
    /// content instead of duplicating rows.
    /// </summary>
    private async Task CopySummaryToContentAsync(
        Report report, Guid jobId, string? rawOutput, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawOutput)) return;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(rawOutput); }
        catch (JsonException) { return; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

            string? summary = doc.RootElement.TryGetProperty("summary", out var sEl) && sEl.ValueKind == JsonValueKind.String
                ? sEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(summary)) return; // ingest-only job, nothing to copy

            var keyFindings = ExtractStringArray(doc.RootElement, "key_findings");
            var topics = ExtractStringArray(doc.RootElement, "topics");

            var lang = string.IsNullOrWhiteSpace(report.OriginalLanguage) ? "ar" : report.OriginalLanguage;
            var existing = await _db.ReportAiContents
                .FirstOrDefaultAsync(c => c.ReportId == report.Id && c.Language == lang, ct);

            if (existing is null)
            {
                _db.ReportAiContents.Add(new ReportAiContent
                {
                    ReportId = report.Id,
                    Language = lang,
                    AiJobId = jobId,
                    Summary = summary,
                    KeyFindings = JsonSerializer.Serialize(keyFindings),
                    Indicators = JsonSerializer.Serialize(topics),
                    GeneratedAt = DateTime.UtcNow,
                });
            }
            else
            {
                existing.Summary = summary;
                existing.KeyFindings = JsonSerializer.Serialize(keyFindings);
                existing.Indicators = JsonSerializer.Serialize(topics);
                existing.GeneratedAt = DateTime.UtcNow;
                existing.AiJobId = jobId;
            }
        }
    }

    private static List<string> ExtractStringArray(JsonElement root, string key)
    {
        var list = new List<string>();
        if (!root.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
        }
        return list;
    }

    private static int? TryReadIntFromJson(string? raw, string key)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty(key, out var prop)) return null;
            return prop.ValueKind switch
            {
                JsonValueKind.Number when prop.TryGetInt32(out var i) => i,
                JsonValueKind.Number => (int)prop.GetDouble(),
                JsonValueKind.String when int.TryParse(prop.GetString(), out var p) => p,
                _ => (int?)null,
            };
        }
        catch (JsonException) { return null; }
    }

    private static string? TryReadStringFromJson(string? raw, string key)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return doc.RootElement.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String
                ? prop.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

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
        {
            _logger.LogInformation("[ai] ToGcsUri: already gs://, passthrough: {Uri}", objectKey);
            return objectKey;
        }
        if (string.IsNullOrWhiteSpace(_storage.GcsBucketName))
            throw new InvalidOperationException("GcsBucketName is not configured — cannot construct gs:// URI for ai-service.");
        var uri = $"gs://{_storage.GcsBucketName}/{objectKey.TrimStart('/')}";
        _logger.LogInformation("[ai] ToGcsUri: {Key} -> {Uri}", objectKey, uri);
        return uri;
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
