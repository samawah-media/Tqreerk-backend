using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.Common;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.AI.Jobs;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

/// <summary>
/// Owns the parse/queue/list side of the bulk-import feature. The actual
/// fetch + AI work is in <see cref="BulkImportProcessor"/> — this service
/// is intentionally synchronous and database-only so the request thread
/// never blocks on network I/O.
/// </summary>
public class BulkImportService : IBulkImportService
{
    /// <summary>Hard cap per Excel — matches the stated requirement.</summary>
    public const int MaxRowsPerJob = 5000;

    /// <summary>
    /// Column headers the parser expects (exactly, case-sensitive). The
    /// template generator emits the same list so download → fill → upload
    /// round-trips losslessly.
    /// </summary>
    public static readonly string[] ExpectedHeaders =
    {
        "title",
        "title_en",
        "file_url",
        "sector_name_ar",
        "report_type",
        "country_name_ar",
        "publication_year",
        "authors",
        "original_language",
        "source",
        "keywords",
    };

    private readonly TaqreerkDbContext _db;
    private readonly IBackgroundJobClient _jobClient;
    private readonly ILogger<BulkImportService> _logger;

    public BulkImportService(
        TaqreerkDbContext db,
        IBackgroundJobClient jobClient,
        ILogger<BulkImportService> logger)
    {
        _db = db;
        _jobClient = jobClient;
        _logger = logger;
    }

    public async Task<Guid> CreateJobAsync(
        Guid adminUserId,
        Stream excelStream,
        string fileName,
        CancellationToken ct = default)
    {
        var rows = ParseExcel(excelStream);
        if (rows.Count == 0)
            throw new InvalidOperationException("Excel فارغ — لا يوجد صفوف صالحة.");

        if (rows.Count > MaxRowsPerJob)
            throw new InvalidOperationException(
                $"الحد الأقصى {MaxRowsPerJob} تقارير لكل عملية رفع. الملف يحتوي على {rows.Count} صف.");

        // Resolve the platform "publisher" org (lazily get-or-create on the
        // first ever bulk import). Reports must have an OrganizationId, but
        // bulk-imported third-party reports aren't owned by any real org —
        // we route them through a synthetic platform org so the FK + downstream
        // queries (org-scoped reports list, etc.) keep working unchanged.
        var orgId = await GetOrCreatePlatformOrgAsync(adminUserId, ct);

        var job = new BulkImportJob
        {
            CreatedByUserId = adminUserId,
            OrganizationId = orgId,
            TotalCount = rows.Count,
            SourceFileName = fileName,
            Status = BulkImportStatus.Pending,
        };

        // Per-row snapshot — stored regardless of validation outcome so the
        // admin UI can render the Excel even before the processor picks it up.
        // Validation errors (missing title / bad URL) flip the row to Failed
        // up-front instead of waiting for the processor to do it.
        foreach (var (row, idx) in rows.Select((r, i) => (r, i + 1)))
        {
            var (stage, error) = ValidateRow(row);
            job.Items.Add(new BulkImportItem
            {
                RowIndex = idx,
                Stage = stage,
                ErrorMessage = error,
                Title = (row.Title ?? string.Empty).Trim(),
                TitleEn = (row.TitleEn ?? string.Empty).Trim(),
                FileUrl = (row.FileUrl ?? string.Empty).Trim(),
                ReportType = NullIfBlank(row.ReportType),
                Source = NullIfBlank(row.Source),
                Authors = NullIfBlank(row.Authors),
                OriginalLanguage = NormalizeLanguage(row.OriginalLanguage),
                PublicationYear = row.PublicationYear,
                SectorNameAr = NullIfBlank(row.SectorNameAr),
                CountryNameAr = NullIfBlank(row.CountryNameAr),
                Keywords = NullIfBlank(row.Keywords),
                StartedAt = stage == BulkImportItemStage.Failed ? DateTime.UtcNow : null,
                CompletedAt = stage == BulkImportItemStage.Failed ? DateTime.UtcNow : null,
            });
            BulkImportKeywordsCache.Set(job.Id, idx, NullIfBlank(row.Keywords));
        }

        // Pre-populate counters — rows that failed validation up-front
        // already count as Failed; Hangfire jobs increment the rest as they
        // complete.
        job.FailedCount = job.Items.Count(i => i.Stage == BulkImportItemStage.Failed);

        var pendingItems = job.Items
            .Where(i => i.Stage == BulkImportItemStage.Pending)
            .ToList();

        // Mark job as Processing immediately — no need to wait for the first
        // Hangfire tick to flip it. If all items failed validation, mark it
        // Completed right away so the job doesn't sit in Processing forever.
        if (pendingItems.Count == 0)
        {
            job.Status      = BulkImportStatus.Completed;
            job.StartedAt   = DateTime.UtcNow;
            job.CompletedAt = DateTime.UtcNow;
        }
        else
        {
            job.Status    = BulkImportStatus.Processing;
            job.StartedAt = DateTime.UtcNow;
        }

        _db.BulkImportJobs.Add(job);
        await _db.SaveChangesAsync(ct);

        // Enqueue one Hangfire job per Pending item. Hangfire persists the
        // job in its own PostgreSQL tables, so items survive worker restarts
        // and Cloud Run scale events without needing the stuck-upload recovery
        // logic that the old BackgroundService required.
        foreach (var item in pendingItems)
            _jobClient.Enqueue<BulkUploadItemJob>(j => j.ExecuteAsync(item.Id, CancellationToken.None));

        _logger.LogInformation(
            "[bulk-import] queued job={JobId} admin={AdminId} rows={Total} failed_validation={FailedAtParse} enqueued={Enqueued}",
            job.Id, adminUserId, job.TotalCount, job.FailedCount, pendingItems.Count);

        return job.Id;
    }

    public async Task<BulkImportJobDetailDto?> GetJobAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await _db.BulkImportJobs
            .AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => new
            {
                j.Id,
                j.CreatedAt,
                j.Status,
                j.TotalCount,
                j.CompletedCount,
                j.FailedCount,
                j.SourceFileName,
                j.ErrorMessage,
                j.StartedAt,
                j.CompletedAt,
                j.AccumulatedSeconds,
                Items = j.Items.OrderBy(i => i.RowIndex).Select(i => new
                {
                    i.Id, i.RowIndex, i.Stage,
                    i.Title, i.TitleEn, i.FileUrl, i.Source, i.Authors,
                    i.OriginalLanguage, i.PublicationYear,
                    i.SectorNameAr, i.CountryNameAr, i.ReportType,
                    i.ReportId,
                    ReportSlug = i.Report != null ? i.Report.Slug : null,
                    i.ErrorMessage, i.StartedAt, i.CompletedAt,
                }).ToList(),
            })
            .FirstOrDefaultAsync(ct);

        if (job is null) return null;

        var reportIds = job.Items
            .Where(i => i.ReportId.HasValue)
            .Select(i => i.ReportId!.Value)
            .Distinct()
            .ToList();

        Dictionary<Guid, string> keywordsByReport;
        if (reportIds.Count == 0)
        {
            keywordsByReport = new Dictionary<Guid, string>();
        }
        else
        {
            var keywordRows = await _db.ReportKeywords
                .AsNoTracking()
                .Where(k => reportIds.Contains(k.ReportId))
                .OrderBy(k => k.ReportId)
                .ThenBy(k => k.Keyword)
                .Select(k => new { k.ReportId, k.Keyword })
                .ToListAsync(ct);

            keywordsByReport = keywordRows
                .GroupBy(k => k.ReportId)
                .ToDictionary(
                    g => g.Key,
                    g => string.Join("، ", g.Select(x => x.Keyword)));
        }

        var items = job.Items
            .Select(i =>
            {
                string? keywords = null;
                if (i.ReportId is { } rid && keywordsByReport.TryGetValue(rid, out var saved))
                    keywords = saved;
                else
                    keywords = BulkImportKeywordsCache.Get(jobId, i.RowIndex);

                return new BulkImportItemDto(
                    i.Id, i.RowIndex, i.Stage.ToString(),
                    i.Title, i.TitleEn, i.FileUrl, i.Source, i.Authors,
                    i.OriginalLanguage, i.PublicationYear,
                    i.SectorNameAr, i.CountryNameAr, i.ReportType, keywords,
                    i.ReportId, i.ReportSlug,
                    i.ErrorMessage, i.StartedAt, i.CompletedAt);
            })
            .ToList();

        return new BulkImportJobDetailDto(
            job.Id, job.CreatedAt, job.Status.ToString(),
            job.TotalCount, job.CompletedCount, job.FailedCount,
            job.SourceFileName, job.ErrorMessage,
            job.StartedAt, job.CompletedAt,
            job.AccumulatedSeconds,
            items);
    }

    public async Task<IReadOnlyList<BulkImportJobSummaryDto>> ListJobsAsync(
        Guid adminUserId, int take = 20, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 100);
        return await _db.BulkImportJobs
            .AsNoTracking()
            .Where(j => j.CreatedByUserId == adminUserId)
            .OrderByDescending(j => j.CreatedAt)
            .Take(take)
            .Select(j => new BulkImportJobSummaryDto(
                j.Id, j.CreatedAt, j.Status.ToString(),
                j.TotalCount, j.CompletedCount, j.FailedCount,
                j.SourceFileName, j.ErrorMessage,
                j.StartedAt, j.CompletedAt,
                j.AccumulatedSeconds))
            .ToListAsync(ct);
    }

    public byte[] GenerateTemplate()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Reports");

        // Header row — exact same names the parser keys off.
        for (var i = 0; i < ExpectedHeaders.Length; i++)
        {
            var cell = sheet.Cell(1, i + 1);
            cell.Value = ExpectedHeaders[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromArgb(245, 245, 245);
        }

        // One example row so the admin can see the expected formats. We
        // leave it as comment-only example values, not fake data, so the
        // admin remembers to delete it before saving. Column order MUST
        // mirror ExpectedHeaders above.
        sheet.Cell(2, 1).Value = "تقرير الحالة الاقتصادية 2025";
        sheet.Cell(2, 2).Value = "Economic Status Report 2025";
        sheet.Cell(2, 3).Value = "https://example.com/report.pdf";
        sheet.Cell(2, 4).Value = "اقتصاد";
        sheet.Cell(2, 5).Value = "تقرير سنوي";
        sheet.Cell(2, 6).Value = "السعودية";
        sheet.Cell(2, 7).Value = 2025;
        sheet.Cell(2, 8).Value = "أحمد محمد، فاطمة علي";
        sheet.Cell(2, 9).Value = "ar";
        sheet.Cell(2, 10).Value = "البنك الدولي";
        sheet.Cell(2, 11).Value = "اقتصاد، نمو، استثمار";

        sheet.Columns().AdjustToContents();

        // RTL feels native for the Arabic-first audience; ClosedXML also
        // honours it at render-time in Excel.
        sheet.RightToLeft = true;

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    public async Task<byte[]?> GenerateFailedExportAsync(Guid jobId, CancellationToken ct = default)
    {
        var failedItems = await _db.BulkImportItems
            .AsNoTracking()
            .Where(i => i.JobId == jobId && i.Stage == BulkImportItemStage.Failed)
            .OrderBy(i => i.RowIndex)
            .ToListAsync(ct);

        if (failedItems.Count == 0) return null;

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Failed Rows");

        var headers = ExpectedHeaders.Append("error_message").ToArray();
        for (var i = 0; i < headers.Length; i++)
        {
            var cell = sheet.Cell(1, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromArgb(255, 220, 220);
        }

        for (var r = 0; r < failedItems.Count; r++)
        {
            var item = failedItems[r];
            var row = r + 2;
            sheet.Cell(row, 1).Value = item.Title;
            sheet.Cell(row, 2).Value = item.TitleEn;
            sheet.Cell(row, 3).Value = item.FileUrl;
            sheet.Cell(row, 4).Value = item.SectorNameAr ?? string.Empty;
            sheet.Cell(row, 5).Value = item.ReportType ?? string.Empty;
            sheet.Cell(row, 6).Value = item.CountryNameAr ?? string.Empty;
            if (item.PublicationYear.HasValue)
                sheet.Cell(row, 7).Value = item.PublicationYear.Value;
            else
                sheet.Cell(row, 7).Value = string.Empty;
            sheet.Cell(row, 8).Value = item.Authors ?? string.Empty;
            sheet.Cell(row, 9).Value = item.OriginalLanguage ?? string.Empty;
            sheet.Cell(row, 10).Value = item.Source ?? string.Empty;
            sheet.Cell(row, 11).Value = item.Keywords ?? string.Empty;
            sheet.Cell(row, 12).Value = item.ErrorMessage ?? string.Empty;
        }

        sheet.Columns().AdjustToContents();
        sheet.RightToLeft = true;

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    public async Task<int> RetryFailedAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await _db.BulkImportJobs
            .Include(j => j.Items)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct)
            ?? throw new KeyNotFoundException("Bulk import job not found.");

        // Include items stuck in active stages (Uploading/Ingesting/Summarizing)
        // for longer than 30 min — these are orphaned by a crashed worker and
        // will not self-recover until Hangfire's invisibility timeout expires.
        // Resetting them here re-enqueues immediately instead of waiting.
        var stuckThreshold = DateTime.UtcNow.AddMinutes(-30);
        var failed = job.Items.Where(i =>
            i.Stage == BulkImportItemStage.Failed ||
            ((i.Stage == BulkImportItemStage.Uploading ||
              i.Stage == BulkImportItemStage.Ingesting ||
              i.Stage == BulkImportItemStage.Summarizing) &&
             i.StartedAt < stuckThreshold)).ToList();
        if (failed.Count == 0) return 0;

        foreach (var item in failed)
        {
            // Wipe terminal-state breadcrumbs. The processor's stage-aware
            // dispatch picks the right entry point based on what's NULL:
            //   - ReportId NULL          → restart from upload
            //   - ReportId set, no Ingest→ restart from ingest
            //   - Ingest set, no Sum.    → wait for ingest, then summarize
            // We deliberately don't try to be clever about which stage
            // failed — let the processor re-derive from the data.
            item.Stage = BulkImportItemStage.Pending;
            item.ErrorMessage = null;
            item.StartedAt = null;
            item.CompletedAt = null;

            // If the failure happened mid-AI-pipeline (we have a stale
            // ai_jobs row that's Failed), drop the cursor so the next
            // tick re-enqueues a fresh job instead of re-polling the dead
            // one forever. Same logic for summarize.
            if (item.IngestJobId is { } ingestId)
            {
                var ingestStillFailed = await _db.AiJobs
                    .AnyAsync(j => j.Id == ingestId && j.Status == AiJobStatus.Failed, ct);
                if (ingestStillFailed) item.IngestJobId = null;
            }
            if (item.SummarizeJobId is { } summarizeId)
            {
                var summarizeStillFailed = await _db.AiJobs
                    .AnyAsync(j => j.Id == summarizeId && j.Status == AiJobStatus.Failed, ct);
                if (summarizeStillFailed) item.SummarizeJobId = null;
            }
        }

        // Decrement the cached counter — the processor refreshes it on
        // each per-item terminal transition, but we own this one because
        // we're rolling state backward.
        job.FailedCount = Math.Max(0, job.FailedCount - failed.Count);

        // Bank the elapsed time from the current (ending) run before resetting
        // the clock so the UI can show cumulative total across all retries.
        if (job.StartedAt is { } ranSince)
            job.AccumulatedSeconds += (long)(DateTime.UtcNow - ranSince).TotalSeconds;

        job.StartedAt    = DateTime.UtcNow;
        job.CompletedAt  = null;
        job.ErrorMessage = null;
        if (job.Status == BulkImportStatus.Completed
            || job.Status == BulkImportStatus.Failed)
        {
            job.Status = BulkImportStatus.Processing;
        }

        await _db.SaveChangesAsync(ct);

        // Enqueue a fresh Hangfire upload job for each reset item. The
        // job handles resume paths (ReportId already set, chunks present,
        // etc.) so retries start at the right stage automatically.
        foreach (var item in failed)
            _jobClient.Enqueue<BulkUploadItemJob>(j => j.ExecuteAsync(item.Id, CancellationToken.None));

        _logger.LogInformation(
            "[bulk-import] retry job={JobId} reset {Count} failed item(s) to Pending and enqueued",
            jobId, failed.Count);
        return failed.Count;
    }

    public async Task<int> CancelJobAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await _db.BulkImportJobs
            .FirstOrDefaultAsync(j => j.Id == jobId, ct)
            ?? throw new KeyNotFoundException("Bulk import job not found.");

        if (job.Status is BulkImportStatus.Completed or BulkImportStatus.Failed)
            return 0;

        // Atomically fail every item still in an active stage. ExecuteUpdateAsync
        // goes straight to the DB — no need to load 5000 rows into memory.
        var cancelled = await _db.BulkImportItems
            .Where(i => i.JobId == jobId
                     && i.Stage != BulkImportItemStage.Completed
                     && i.Stage != BulkImportItemStage.Failed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Stage, BulkImportItemStage.Failed)
                .SetProperty(i => i.ErrorMessage, "تم إلغاء المهمة")
                .SetProperty(i => i.CompletedAt, DateTime.UtcNow), ct);

        // Recalculate counters from DB truth rather than guessing deltas.
        job.CompletedCount = await _db.BulkImportItems
            .CountAsync(i => i.JobId == jobId && i.Stage == BulkImportItemStage.Completed, ct);
        job.FailedCount = await _db.BulkImportItems
            .CountAsync(i => i.JobId == jobId && i.Stage == BulkImportItemStage.Failed, ct);
        job.Status = BulkImportStatus.Failed;
        job.CompletedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "[bulk-import] cancel job={JobId} stopped {Count} active item(s)",
            jobId, cancelled);
        return cancelled;
    }

    // ── Excel parsing ──────────────────────────────────────────────────────

    private record ParsedRow(
        string? Title,
        string? TitleEn,
        string? FileUrl,
        string? SectorNameAr,
        string? ReportType,
        string? CountryNameAr,
        int? PublicationYear,
        string? Authors,
        string? OriginalLanguage,
        string? Source,
        string? Keywords);

    private List<ParsedRow> ParseExcel(Stream excelStream)
    {
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(excelStream);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"تعذّر قراءة ملف Excel: {ex.Message}");
        }

        using (workbook)
        {
            var sheet = workbook.Worksheet(1)
                ?? throw new InvalidOperationException("لا توجد ورقة في الملف.");

            // Find the header row (must be row 1; we don't try to be clever
            // about Excel files with a title banner above the headers).
            var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var headerRow = sheet.Row(1);
            foreach (var cell in headerRow.CellsUsed())
            {
                var name = cell.GetString().Trim();
                if (string.IsNullOrEmpty(name)) continue;
                headerMap[name] = cell.Address.ColumnNumber;
            }

            // Required headers for the parser to know which column is which.
            // Optional columns can be missing entirely — the row falls back
            // to null for those fields.
            foreach (var required in new[] { "title", "title_en", "file_url" })
            {
                if (!headerMap.ContainsKey(required))
                    throw new InvalidOperationException(
                        $"عمود {required} غير موجود في الملف. الأعمدة المطلوبة: title, title_en, file_url.");
            }

            var rows = new List<ParsedRow>();
            // ClosedXML's RowsUsed() includes the header — start from row 2.
            // Cap iteration at MaxRowsPerJob + a small tolerance for blank
            // tail rows so a sheet padded with phantom empties still parses
            // cleanly; the in-loop check below rejects real data overflow.
            var lastUsed = sheet.LastRowUsed()?.RowNumber() ?? 1;
            var dataLimit = Math.Min(lastUsed, MaxRowsPerJob + 50);
            for (var r = 2; r <= dataLimit; r++)
            {
                var row = sheet.Row(r);
                if (row.IsEmpty()) continue;

                var title = ReadCell(row, headerMap, "title");
                var titleEn = ReadCell(row, headerMap, "title_en");
                var fileUrl = ReadCell(row, headerMap, "file_url");

                // Treat a row with no title (ar or en) and no file_url as a
                // blank tail row (Excel files often have phantom empty rows
                // below the last data row).
                if (string.IsNullOrWhiteSpace(title)
                    && string.IsNullOrWhiteSpace(titleEn)
                    && string.IsNullOrWhiteSpace(fileUrl))
                    continue;

                rows.Add(new ParsedRow(
                    title,
                    titleEn,
                    fileUrl,
                    ReadCell(row, headerMap, "sector_name_ar"),
                    ReadCell(row, headerMap, "report_type"),
                    ReadCell(row, headerMap, "country_name_ar"),
                    ReadIntCell(row, headerMap, "publication_year"),
                    ReadCell(row, headerMap, "authors"),
                    ReadCell(row, headerMap, "original_language"),
                    ReadCell(row, headerMap, "source"),
                    ReadCell(row, headerMap, "keywords")));

                if (rows.Count > MaxRowsPerJob)
                {
                    // Bail loudly — tells the admin the cap was breached
                    // instead of silently truncating.
                    throw new InvalidOperationException(
                        $"الحد الأقصى {MaxRowsPerJob} تقارير لكل عملية رفع.");
                }
            }

            return rows;
        }
    }

    private static string? ReadCell(IXLRow row, Dictionary<string, int> headerMap, string name)
    {
        if (!headerMap.TryGetValue(name, out var col)) return null;
        var cell = row.Cell(col);
        if (cell.IsEmpty()) return null;
        var s = cell.GetString().Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static int? ReadIntCell(IXLRow row, Dictionary<string, int> headerMap, string name)
    {
        if (!headerMap.TryGetValue(name, out var col)) return null;
        var cell = row.Cell(col);
        if (cell.IsEmpty()) return null;
        // ClosedXML hands us either a number or a string; try both paths.
        if (cell.DataType == XLDataType.Number) return (int)cell.GetDouble();
        var s = cell.GetString().Trim();
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
    }

    // ── Per-row validation (cheap, structural; the processor still re-checks
    //    the URL + PDF when it actually fetches) ────────────────────────────

    private static (BulkImportItemStage stage, string? error) ValidateRow(ParsedRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Title))
            return (BulkImportItemStage.Failed, "العنوان العربي (title) مطلوب.");

        if (string.IsNullOrWhiteSpace(row.TitleEn))
            return (BulkImportItemStage.Failed, "العنوان الإنجليزي (title_en) مطلوب.");

        if (string.IsNullOrWhiteSpace(row.FileUrl))
            return (BulkImportItemStage.Failed, "رابط الملف (file_url) مطلوب.");

        if (!Uri.TryCreate(row.FileUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return (BulkImportItemStage.Failed,
                "رابط الملف غير صالح — يجب أن يبدأ بـ http:// أو https://.");
        }

        return (BulkImportItemStage.Pending, null);
    }

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? NormalizeLanguage(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var lang = s.Trim().ToLowerInvariant();
        return lang is "ar" or "en" ? lang : null;
    }

    // ── Platform org bootstrap (sync, idempotent) ────────────────────────────

    private const string PlatformOrgSlug = "taqreerk-platform";

    /// <summary>
    /// Get-or-create the synthetic "platform" organisation that owns
    /// bulk-imported third-party reports. We don't seed it on migration
    /// because the seed bundle was already shipped; this is a one-shot
    /// lazy-init that runs at most once across the whole platform's
    /// lifetime.
    /// </summary>
    private async Task<Guid> GetOrCreatePlatformOrgAsync(Guid adminUserId, CancellationToken ct)
    {
        var existing = await _db.Organizations
            .IgnoreQueryFilters()
            .Where(o => o.Slug == PlatformOrgSlug)
            .Select(o => (Guid?)o.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is not null) return existing.Value;

        var org = new Organization
        {
            NameAr = "منصة تقريرك",
            NameEn = "Taqreerk Platform",
            Slug = PlatformOrgSlug,
            Type = OrganizationType.Governmental, // closest existing enum value
            Status = OrganizationStatus.Active,
            IsVerified = true,
            CreatedByUserId = adminUserId,
        };
        _db.Organizations.Add(org);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[bulk-import] auto-created platform org={OrgId} for bulk-imported reports", org.Id);
        return org.Id;
    }
}
