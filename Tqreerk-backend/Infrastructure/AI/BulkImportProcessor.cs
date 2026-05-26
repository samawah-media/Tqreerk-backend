using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PDFtoImage;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Settings;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;
using Taqreerk.Infrastructure.Storage;

namespace Taqreerk.Infrastructure.AI;

/// <summary>
/// Background worker that drives bulk-import jobs through the pipeline:
///
///   1. Pending items   → fetch the PDF from <c>FileUrl</c>, upload to GCS,
///                        render the first page as a temporary cover image,
///                        create a <c>Report</c> row, then call
///                        <c>/bulk/ingest</c> on the AI service.
///   2. Ingesting items → poll <c>ai_jobs</c> for the linked Ingestion job;
///                        when Completed, fire <c>/bulk/summarize</c>.
///   3. Summarizing     → poll the linked Summarization job; on success,
///                        copy the summary into <c>report_ai_contents</c>
///                        and flip the <c>Report.Status</c> to
///                        <c>Published</c>.
///
/// Each item progresses independently — a single bad URL fails one row,
/// not the whole batch. The job's aggregate counters
/// (<c>CompletedCount</c>, <c>FailedCount</c>) are refreshed after each
/// state transition so the admin progress UI doesn't have to aggregate
/// items on every poll.
/// </summary>
public class BulkImportProcessor : BackgroundService
{
    /// <summary>How often the processor scans for work. Cheap query (a few
    /// indexed predicates), so 10 s is fine — gives the admin UI a quick-enough
    /// "started moving" signal without hammering the DB.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    /// <summary>Hard cap per fetch so a misconfigured URL pointing at a 5 GB
    /// file doesn't OOM the worker. Mirrors the manual-upload cap in
    /// ReportsController (200 MB).</summary>
    private const long MaxFetchBytes = 200L * 1024 * 1024;

    /// Named client key registered in ServiceExtensions; we resolve via
    /// the factory each time we fetch so HttpMessageHandler lifetime
    /// rotation works correctly for this long-running BackgroundService
    /// (a captured HttpClient on a singleton never refreshes its handler).
    public const string HttpClientName = "BulkImportProcessor";

    private readonly IServiceProvider _services;
    private readonly ILogger<BulkImportProcessor> _logger;
    private readonly IHttpClientFactory _httpFactory;

    public BulkImportProcessor(
        IServiceProvider services,
        IHttpClientFactory httpFactory,
        ILogger<BulkImportProcessor> logger)
    {
        _services = services;
        _logger = logger;
        _httpFactory = httpFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "BulkImportProcessor started — polling every {Seconds}s",
            PollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                // Worker MUST never die — a single bad tick is logged and we
                // sleep until the next interval.
                _logger.LogError(ex, "BulkImportProcessor tick failed");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("BulkImportProcessor stopped");
    }

    /// <summary>
    /// One full sweep. We split the work by stage and process them serially
    /// per-job — Pending → Ingesting → Summarizing — so each tick advances
    /// the job by one stage at most. Cheap; the admin UI polls fast enough
    /// that the user-perceived time-to-progress is bounded by the AI side.
    /// </summary>
    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaqreerkDbContext>();
        var files = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        var ai = scope.ServiceProvider.GetRequiredService<IAiServiceClient>();
        var storageOpts = scope.ServiceProvider.GetRequiredService<IOptions<FileStorageSettings>>().Value;

        // 1. Newly-queued jobs — kick off the upload stage for each pending item.
        var pendingJobs = await db.BulkImportJobs
            .Where(j => j.Status == BulkImportStatus.Pending)
            .Include(j => j.Items)
            .ToListAsync(ct);
        foreach (var job in pendingJobs)
        {
            await ProcessPendingJobAsync(db, files, ai, storageOpts, job, ct);
        }

        // 2. In-flight jobs — advance Ingesting → Summarizing → Completed
        //    as their respective ai_jobs rows reach terminal state. Also
        //    pick up any Pending items inside a Processing job; the only
        //    way to land in that state is via the retry endpoint, which
        //    rolls Failed items back to Pending without flipping the job.
        var inFlightJobs = await db.BulkImportJobs
            .Where(j => j.Status == BulkImportStatus.Processing)
            .Include(j => j.Items)
            .ToListAsync(ct);
        foreach (var job in inFlightJobs)
        {
            // Retry-driven Pending items first — they can then move into
            // Ingesting in the same tick so the admin's progress UI shows
            // the row advancing immediately.
            if (job.Items.Any(i => i.Stage == BulkImportItemStage.Pending))
            {
                await ProcessPendingItemsAsync(db, files, ai, storageOpts, job, ct);
            }
            await AdvanceInFlightJobAsync(db, ai, job, ct);
        }
    }

    // ── Stage 1: Pending → Uploading → Ingesting ────────────────────────────

    private async Task ProcessPendingJobAsync(
        TaqreerkDbContext db,
        IFileStorage files,
        IAiServiceClient ai,
        FileStorageSettings storage,
        BulkImportJob job,
        CancellationToken ct)
    {
        _logger.LogInformation("[bulk-import] starting job={JobId}", job.Id);
        job.Status = BulkImportStatus.Processing;
        job.StartedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await ProcessPendingItemsAsync(db, files, ai, storage, job, ct);
    }

    /// <summary>
    /// Drive every Pending item on the job through upload + ingest. Shared
    /// between the initial Pending-job kickoff and the retry path (which
    /// can put items back into Pending while the job itself is already
    /// Processing). Items that already have a <c>ReportId</c> skip the
    /// upload step and go straight to ingest — that's the case where the
    /// row failed mid-pipeline and we don't want to re-fetch the PDF.
    /// </summary>
    private async Task ProcessPendingItemsAsync(
        TaqreerkDbContext db,
        IFileStorage files,
        IAiServiceClient ai,
        FileStorageSettings storage,
        BulkImportJob job,
        CancellationToken ct)
    {
        var pendingItems = job.Items
            .Where(i => i.Stage == BulkImportItemStage.Pending)
            .OrderBy(i => i.RowIndex)
            .ToList();
        if (pendingItems.Count == 0) return;

        foreach (var item in pendingItems)
        {
            ct.ThrowIfCancellationRequested();

            // Retry path: the item already has a Report row from a prior
            // attempt. Skip re-fetching the PDF and just flip the stage so
            // the bulk-ingest dispatch below picks it up.
            if (item.ReportId.HasValue)
            {
                item.Stage = BulkImportItemStage.Uploading;
                item.StartedAt = DateTime.UtcNow;
                continue;
            }

            try
            {
                await UploadItemAsync(db, files, item, job.OrganizationId, job.CreatedByUserId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[bulk-import] item={ItemId} upload failed: {Msg}",
                    item.Id, ex.Message);
                MarkItemFailed(item, ex.Message);
                job.FailedCount++;
                await db.SaveChangesAsync(ct);
            }
        }
        await db.SaveChangesAsync(ct);

        // Now batch-call /bulk/ingest with every item that successfully
        // reached the Uploading stage. Doing it in one network round-trip
        // is cheaper than per-item /ingest calls and matches what the
        // Python service expects.
        var uploaded = job.Items
            .Where(i => i.Stage == BulkImportItemStage.Uploading && i.ReportId.HasValue)
            .ToList();
        if (uploaded.Count == 0)
        {
            // Everything failed at upload — flip the job to Completed
            // (per-item failures are real outcomes, not job-level errors).
            await FinaliseJobIfDoneAsync(db, job, ct);
            return;
        }

        // Make sure every uploaded row has its Report nav loaded — the
        // retry path skips UploadItemAsync, which is what normally hydrates
        // it, so we lazy-fill any missing ones here.
        var missingReport = uploaded.Where(i => i.Report is null).ToList();
        if (missingReport.Count > 0)
        {
            var ids = missingReport.Select(i => i.ReportId!.Value).ToList();
            var reports = await db.Reports
                .Where(r => ids.Contains(r.Id))
                .ToListAsync(ct);
            var byId = reports.ToDictionary(r => r.Id);
            foreach (var item in missingReport)
            {
                if (item.ReportId is { } rid && byId.TryGetValue(rid, out var report))
                    item.Report = report;
            }
        }

        try
        {
            var ingestItems = uploaded
                .Select(i => new BulkIngestItem(
                    i.ReportId!.Value,
                    ToGcsUri(i.Report?.FileUrl, storage)))
                .ToList();
            var jobs = await ai.BulkIngestAsync(ingestItems, ct);

            // Map AI-side job_id back onto our items. The AI side returns
            // entries keyed by report_id, so we match on that.
            var byReport = jobs.ToDictionary(j => j.ReportId, j => j.JobId);
            foreach (var item in uploaded)
            {
                if (item.ReportId is { } rid && byReport.TryGetValue(rid, out var ingestJobId))
                {
                    item.IngestJobId = ingestJobId;
                    item.Stage = BulkImportItemStage.Ingesting;
                }
                else
                {
                    MarkItemFailed(item, "AI service did not accept the ingest request.");
                    job.FailedCount++;
                }
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Whole-batch failure on the AI side — fail every uploaded item
            // so the admin doesn't end up with rows stuck in Uploading.
            _logger.LogError(ex, "[bulk-import] /bulk/ingest failed for job={JobId}", job.Id);
            foreach (var item in uploaded)
            {
                MarkItemFailed(item, $"تعذّر بدء معالجة الذكاء الاصطناعي: {ex.Message}");
                job.FailedCount++;
            }
            await db.SaveChangesAsync(ct);
            await FinaliseJobIfDoneAsync(db, job, ct);
        }
    }

    /// <summary>
    /// Per-item upload: fetch the PDF, upload to GCS, render its first page
    /// as a temp cover, create the Report row. Throws on any failure so the
    /// caller flips the row to Failed.
    /// </summary>
    private async Task UploadItemAsync(
        TaqreerkDbContext db,
        IFileStorage files,
        BulkImportItem item,
        Guid orgId,
        Guid uploaderUserId,
        CancellationToken ct)
    {
        // Resume-from-existing gate. Scoped to the platform org because
        // bulk-imports only ever land there — a report with the same
        // title under a real organisation is the org's own work and
        // doesn't conflict. Case-insensitive + trimmed so trailing
        // whitespace or capitalisation in the Excel doesn't slip a
        // duplicate past us. Soft-deleted rows are intentionally NOT
        // counted (the global query filter on Reports already drops
        // them) so re-uploading after an admin deletion works.
        //
        // Instead of failing the row outright, we attach it to the
        // existing Report and resume the AI pipeline from whichever
        // stage that report stalled at. The processor's later stages
        // pick the row up from there exactly as if a fresh upload
        // produced it.
        var normalisedTitle = (item.Title ?? string.Empty).Trim();
        if (normalisedTitle.Length > 0)
        {
            var existing = await db.Reports
                .Where(r => r.OrganizationId == orgId
                         && r.TitleAr.ToLower() == normalisedTitle.ToLower())
                .Select(r => new { r.Id, r.Status })
                .FirstOrDefaultAsync(ct);
            if (existing is not null)
            {
                await ResumeExistingReportAsync(db, item, existing.Id, ct);
                return;
            }
        }

        item.Stage = BulkImportItemStage.Uploading;
        item.StartedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        // 1. Fetch the PDF. Resolve the named client per-fetch so the
        //    factory's handler-lifetime rotation actually fires (a
        //    singleton-captured HttpClient on a BackgroundService would
        //    pin one handler for the lifetime of the process).
        byte[] pdfBytes;
        string contentType;
        try
        {
            using var http = _httpFactory.CreateClient(HttpClientName);
            using var resp = await http.GetAsync(item.FileUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"تعذّر تحميل الملف من الرابط (HTTP {(int)resp.StatusCode}).");

            // Refuse > MaxFetchBytes early when the server reports a length;
            // for chunked transfers we still cap at the read loop below.
            var declaredLength = resp.Content.Headers.ContentLength;
            if (declaredLength is > MaxFetchBytes)
                throw new InvalidOperationException(
                    $"حجم الملف ({declaredLength / 1024 / 1024} MB) يتجاوز الحد المسموح به.");

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int read;
            long total = 0;
            while ((read = await src.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                total += read;
                if (total > MaxFetchBytes)
                    throw new InvalidOperationException(
                        $"حجم الملف يتجاوز الحد المسموح به ({MaxFetchBytes / 1024 / 1024} MB).");
                ms.Write(buffer, 0, read);
            }
            pdfBytes = ms.ToArray();
            contentType = resp.Content.Headers.ContentType?.MediaType ?? "application/pdf";
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"تعذّر الوصول للرابط: {ex.Message}", ex);
        }

        if (pdfBytes.Length < 1024)
            throw new InvalidOperationException("الملف فارغ أو تالف.");

        // 2. Generate slug + upload original PDF.
        var slug = await GenerateUniqueSlugAsync(db, item.Title, ct);
        var safeFileName = $"{slug}.pdf";

        using var pdfStream = new MemoryStream(pdfBytes);
        var stored = await files.UploadAsync(
            pdfStream, safeFileName, contentType, $"reports/{orgId}", ct);

        // 3. Render first page as cover (best-effort — failure is non-fatal,
        //    the Report just gets no cover and the user-side falls back to a
        //    placeholder).
        BulkCoverUploadResult? coverUpload = null;
        try
        {
            coverUpload = await RenderAndUploadCoverAsync(files, pdfBytes, slug, orgId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[bulk-import] cover rendering failed for item={ItemId} — proceeding without cover",
                item.Id);
        }

        // 4. Create the Report row, status=Approved so the AI pipeline can
        //    flip it to Published once summarize completes (matches the
        //    org-side moderation flow's post-approval state).
        var report = new Report
        {
            OrganizationId = orgId,
            UploadedByUserId = uploaderUserId,
            // Bulk-import carries both languages per Excel row (both
            // required at parse time); copy each into the matching column
            // on the Report so the SPA renders the locale-correct title.
            TitleAr = item.Title,
            TitleEn = item.TitleEn,
            Slug = slug,
            ReportType = item.ReportType,
            Source = item.Source,
            Authors = item.Authors,
            OriginalLanguage = string.IsNullOrWhiteSpace(item.OriginalLanguage) ? "ar" : item.OriginalLanguage!,
            PublicationYear = item.PublicationYear,
            FileUrl = stored.ObjectKey,
            CoverImageUrl = coverUpload?.MediumKey,
            CoverImageBaseKey = coverUpload?.BaseKey,
            SourceType = ReportSourceType.Platform,
            Status = ReportStatus.Approved,
            SubmittedForReviewAt = DateTime.UtcNow,
        };

        // Resolve sector by Arabic name. If not found, auto-create it so bulk
        // imports never silently drop the sector assignment — the admin can
        // update NameEn / Slug afterwards via the Categories admin page.
        if (!string.IsNullOrWhiteSpace(item.SectorNameAr))
        {
            var sectorId = await db.Sectors
                .Where(s => s.NameAr == item.SectorNameAr)
                .Select(s => (Guid?)s.Id)
                .FirstOrDefaultAsync(ct);

            if (sectorId is null)
            {
                // Build a URL-safe slug: keep ASCII letters/digits, replace
                // everything else (including Arabic) with hyphens, then append
                // a short random suffix to guarantee uniqueness.
                var sectorSlugBase = new string(item.SectorNameAr
                    .ToLowerInvariant()
                    .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
                    .ToArray())
                    .Trim('-');
                if (string.IsNullOrEmpty(sectorSlugBase)) sectorSlugBase = "sector";
                var sectorSlug = $"{sectorSlugBase}-{Guid.NewGuid().ToString("N")[..8]}";

                var newSector = new Sector
                {
                    Id       = Guid.NewGuid(),
                    NameAr   = item.SectorNameAr,
                    NameEn   = item.SectorNameAr, // placeholder — admin can localise later
                    Slug     = sectorSlug,
                    IsActive = true,
                    SortOrder = 0,
                };
                db.Sectors.Add(newSector);
                await db.SaveChangesAsync(ct);
                sectorId = newSector.Id;
            }

            report.SectorId = sectorId;
        }
        if (!string.IsNullOrWhiteSpace(item.CountryNameAr))
        {
            report.CountryId = await db.Countries
                .Where(c => c.NameAr == item.CountryNameAr)
                .Select(c => (Guid?)c.Id)
                .FirstOrDefaultAsync(ct);
        }

        db.Reports.Add(report);
        await db.SaveChangesAsync(ct);

        item.ReportId = report.Id;
        item.Report = report;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Render the first PDF page and upload three WebP variants (thumb /
    /// medium / full) under <c>public/covers/{coverId}</c>. PDFtoImage uses
    /// PDFium under the hood — works on Linux Cloud Run without extra
    /// setup. We cap at the first page (the admin can upload a real cover
    /// later via the report edit flow).
    /// </summary>
    private async Task<BulkCoverUploadResult?> RenderAndUploadCoverAsync(
        IFileStorage files, byte[] pdfBytes, string slug, Guid orgId, CancellationToken ct)
    {
        // PDFtoImage's API is sync; offload to a worker thread so we don't
        // block the polling loop's task scheduler. Resize happens here too
        // so the heavy SkiaSharp work all runs off the scheduler.
        var variants = await Task.Run(() =>
        {
            using var pdfStream = new MemoryStream(pdfBytes);
            using var bitmap = Conversion.ToImage(pdfStream, page: 0);
            return CoverImageVariants.Generate(bitmap);
        }, ct);

        var coverId = Guid.NewGuid().ToString("N");
        var folder = $"public/covers/{coverId}";

        var thumb = await files.UploadPublicAsync(
            new MemoryStream(variants.Thumb),
            CoverImageVariants.ThumbName, CoverImageEncoder.ContentType, folder, ct);
        var medium = await files.UploadPublicAsync(
            new MemoryStream(variants.Medium),
            CoverImageVariants.MediumName, CoverImageEncoder.ContentType, folder, ct);
        var full = await files.UploadPublicAsync(
            new MemoryStream(variants.Full),
            CoverImageVariants.FullName, CoverImageEncoder.ContentType, folder, ct);

        var baseKey = medium.ObjectKey[..medium.ObjectKey.LastIndexOf('/')];
        _logger.LogInformation(
            "Bulk-import cover variants for slug {Slug} at {BaseKey} (thumb={ThumbB}B, med={MedB}B, full={FullB}B)",
            slug, baseKey, thumb.SizeBytes, medium.SizeBytes, full.SizeBytes);

        return new BulkCoverUploadResult(BaseKey: baseKey, MediumKey: medium.ObjectKey);
    }

    /// <summary>
    /// Bulk-import equivalent of <c>ReportService.CoverUploadResult</c>.
    /// Kept private to this file so the two services stay independent —
    /// bulk import doesn't depend on ReportService.
    /// </summary>
    private sealed record BulkCoverUploadResult(string BaseKey, string MediumKey);

    // ── Stage 2: Ingesting → Summarizing → Completed ───────────────────────

    private async Task AdvanceInFlightJobAsync(
        TaqreerkDbContext db,
        IAiServiceClient ai,
        BulkImportJob job,
        CancellationToken ct)
    {
        // 2a. Items currently in Ingesting — see if their AI ingest job
        //     finished, then chain summarize. Items with IngestJobId=null
        //     are the resume-from-existing path: their Report already has
        //     report_chunks, so we can short-circuit straight to the
        //     summarize dispatch without polling any ai_jobs row.
        var ingesting = job.Items
            .Where(i => i.Stage == BulkImportItemStage.Ingesting)
            .ToList();
        if (ingesting.Count > 0)
        {
            var withJobIds = ingesting
                .Where(i => i.IngestJobId.HasValue)
                .ToList();
            var resumed = ingesting
                .Where(i => !i.IngestJobId.HasValue && i.ReportId.HasValue)
                .ToList();

            var statusById = new Dictionary<Guid, (AiJobStatus Status, string? ErrorMessage, string? OutputData)>();
            if (withJobIds.Count > 0)
            {
                var ingestJobIds = withJobIds.Select(i => i.IngestJobId!.Value).ToList();
                var statuses = await db.AiJobs
                    .Where(j => ingestJobIds.Contains(j.Id))
                    .Select(j => new { j.Id, j.Status, j.ErrorMessage, j.OutputData })
                    .ToListAsync(ct);
                statusById = statuses.ToDictionary(s => s.Id, s => (s.Status, s.ErrorMessage, s.OutputData));
            }

            var readyForSummarize = new List<BulkImportItem>();
            foreach (var item in withJobIds)
            {
                if (!statusById.TryGetValue(item.IngestJobId!.Value, out var st)) continue;

                if (st.Status == AiJobStatus.Completed)
                {
                    // Update PageCount from the ingest output if present —
                    // mirrors what FinalizeCompletedJobsAsync does for org-side
                    // reports.
                    if (item.ReportId is { } rid)
                    {
                        var pages = TryReadInt(st.OutputData, "pages_processed");
                        if (pages is not null)
                        {
                            await db.Reports
                                .Where(r => r.Id == rid)
                                .ExecuteUpdateAsync(s => s.SetProperty(r => r.PageCount, pages.Value), ct);
                        }
                    }
                    readyForSummarize.Add(item);
                }
                else if (st.Status == AiJobStatus.Failed)
                {
                    MarkItemFailed(item, st.ErrorMessage ?? "فشل استخراج محتوى التقرير.");
                    job.FailedCount++;
                }
            }

            // Persist Failed states immediately — before the BulkSummarizeAsync
            // HTTP call below. Without this early save, a transient network or
            // DB error on the summarize call rolls back BOTH the Failed states
            // AND the SummarizeJobIds in one lost SaveChanges, leaving all items
            // stuck in Ingesting forever.
            await db.SaveChangesAsync(ct);

            // Resume path: chunks already exist, so we just need to fire
            // /bulk/summarize for these reports without waiting on an
            // ingest poll. They join the same queue as freshly-completed
            // ingest items below.
            readyForSummarize.AddRange(resumed);

            if (readyForSummarize.Count > 0)
            {
                try
                {
                    var ids = readyForSummarize.Select(i => i.ReportId!.Value).ToList();
                    var summarizeJobs = await ai.BulkSummarizeAsync(ids, ct);
                    var byReport = summarizeJobs.ToDictionary(j => j.ReportId, j => j.JobId);
                    foreach (var item in readyForSummarize)
                    {
                        if (byReport.TryGetValue(item.ReportId!.Value, out var sjId))
                        {
                            item.SummarizeJobId = sjId;
                            item.Stage = BulkImportItemStage.Summarizing;
                        }
                        else
                        {
                            MarkItemFailed(item, "AI service did not accept the summarize request.");
                            job.FailedCount++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "[bulk-import] /bulk/summarize failed for job={JobId}", job.Id);
                    foreach (var item in readyForSummarize)
                    {
                        MarkItemFailed(item, $"تعذّر بدء التلخيص: {ex.Message}");
                        job.FailedCount++;
                    }
                }
            }

            await db.SaveChangesAsync(ct);
        }

        // 2b. Items in Summarizing — see if their summarize job finished,
        //     persist the summary into report_ai_contents, flip to Published.
        var summarizing = job.Items
            .Where(i => i.Stage == BulkImportItemStage.Summarizing && i.SummarizeJobId.HasValue)
            .ToList();
        if (summarizing.Count > 0)
        {
            var summarizeIds = summarizing.Select(i => i.SummarizeJobId!.Value).ToList();
            var statuses = await db.AiJobs
                .Where(j => summarizeIds.Contains(j.Id))
                .Select(j => new { j.Id, j.Status, j.ErrorMessage, j.OutputData })
                .ToListAsync(ct);

            var statusById = statuses.ToDictionary(s => s.Id);
            foreach (var item in summarizing)
            {
                if (!statusById.TryGetValue(item.SummarizeJobId!.Value, out var st)) continue;

                if (st.Status == AiJobStatus.Completed && item.ReportId is { } rid)
                {
                    var report = await db.Reports.FirstOrDefaultAsync(r => r.Id == rid, ct);
                    if (report is null)
                    {
                        MarkItemFailed(item, "Report row disappeared after summarize.");
                        job.FailedCount++;
                        continue;
                    }
                    await CopySummaryToContentAsync(db, report, item.SummarizeJobId.Value, st.OutputData, ct);
                    report.Status = ReportStatus.Published;
                    report.PublishedAt = DateTime.UtcNow;

                    item.Stage = BulkImportItemStage.Completed;
                    item.CompletedAt = DateTime.UtcNow;
                    job.CompletedCount++;
                }
                else if (st.Status == AiJobStatus.Failed)
                {
                    MarkItemFailed(item, st.ErrorMessage ?? "فشل التلخيص.");
                    job.FailedCount++;
                }
            }
            await db.SaveChangesAsync(ct);
        }

        await FinaliseJobIfDoneAsync(db, job, ct);
    }

    private async Task FinaliseJobIfDoneAsync(
        TaqreerkDbContext db, BulkImportJob job, CancellationToken ct)
    {
        var allDone = job.Items.All(i =>
            i.Stage == BulkImportItemStage.Completed
            || i.Stage == BulkImportItemStage.Failed);
        if (!allDone) return;

        // Even when every row failed validation up-front, this is "Completed"
        // (we did process the batch — the items just didn't make it). The
        // counters tell the truth; Status is just "did the batch finish?".
        job.Status = BulkImportStatus.Completed;
        job.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "[bulk-import] job={JobId} finished — completed={Completed} failed={Failed}",
            job.Id, job.CompletedCount, job.FailedCount);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Attach the bulk-import row to a pre-existing Report and put it
    /// back on whichever pipeline stage makes sense for the current
    /// state of that report:
    ///
    ///   • Already Published with content   → Completed (nothing to do).
    ///   • Has chunks but no AR/EN content  → Summarizing (re-run summarize).
    ///   • Has neither                      → Ingesting (re-run ingest+summarize).
    ///
    /// We deliberately reuse the existing AI pipeline plumbing — the
    /// stage we set here is what the next processor tick will pick up;
    /// no separate "resume" code path lives anywhere else.
    /// </summary>
    private async Task ResumeExistingReportAsync(
        TaqreerkDbContext db,
        BulkImportItem item,
        Guid existingReportId,
        CancellationToken ct)
    {
        var hasChunks = await db.ReportChunks
            .AnyAsync(c => c.ReportId == existingReportId, ct);
        var hasContent = await db.ReportAiContents
            .AnyAsync(c => c.ReportId == existingReportId, ct);

        item.ReportId = existingReportId;
        item.StartedAt = DateTime.UtcNow;

        if (hasChunks && hasContent)
        {
            // Already past summarize. Make sure the Report's Status
            // reflects "Published" so a half-promoted row doesn't sit
            // in Approved/ProcessingAi forever — staff explicitly
            // re-uploading the same title means "I want this live".
            var report = await db.Reports.FirstOrDefaultAsync(r => r.Id == existingReportId, ct);
            if (report is not null && report.Status != ReportStatus.Published)
            {
                report.Status = ReportStatus.Published;
                report.PublishedAt ??= DateTime.UtcNow;
            }

            item.Stage = BulkImportItemStage.Completed;
            item.CompletedAt = DateTime.UtcNow;
            item.ErrorMessage = null;
            // Bump the job's success counter — we won't pass through any
            // later branch that would normally increment it.
            item.Job.CompletedCount++;
            _logger.LogInformation(
                "[bulk-import] item={ItemId} reused existing complete report={ReportId}",
                item.Id, existingReportId);
            await db.SaveChangesAsync(ct);
            return;
        }

        if (hasChunks)
        {
            // Ingestion done, summarize wasn't. We park the item in a
            // "ready for summarize" sub-state by setting stage=Ingesting
            // with no IngestJobId — AdvanceInFlightJobAsync has a
            // dedicated branch that picks these up and dispatches a
            // fresh /bulk/summarize call on the next tick.
            item.Stage = BulkImportItemStage.Ingesting;
            item.IngestJobId = null;
            item.SummarizeJobId = null;
            item.ErrorMessage = null;
            _logger.LogInformation(
                "[bulk-import] item={ItemId} reused existing ingested report={ReportId}, resuming at summarize",
                item.Id, existingReportId);
        }
        else
        {
            // Report row exists but ingest never finished (or never ran).
            // Drop the item back to Pending; the bulk-ingest pass on the
            // *next* tick will see ReportId set and skip the upload.
            item.Stage = BulkImportItemStage.Pending;
            item.IngestJobId = null;
            item.SummarizeJobId = null;
            item.ErrorMessage = null;
            _logger.LogInformation(
                "[bulk-import] item={ItemId} reused existing report={ReportId}, resuming at ingest",
                item.Id, existingReportId);
        }

        await db.SaveChangesAsync(ct);
    }

    private static void MarkItemFailed(BulkImportItem item, string error)
    {
        item.Stage = BulkImportItemStage.Failed;
        item.ErrorMessage = (error ?? string.Empty).Length > 4000 ? error![..4000] : error;
        item.CompletedAt = DateTime.UtcNow;
    }

    private static string ToGcsUri(string? objectKey, FileStorageSettings storage)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
            throw new InvalidOperationException("Report has no FileUrl after upload.");
        if (objectKey.StartsWith("gs://", StringComparison.OrdinalIgnoreCase)) return objectKey;
        if (string.IsNullOrWhiteSpace(storage.GcsBucketName))
            throw new InvalidOperationException("GcsBucketName is not configured.");
        return $"gs://{storage.GcsBucketName}/{objectKey.TrimStart('/')}";
    }

    private static async Task<string> GenerateUniqueSlugAsync(TaqreerkDbContext db, string title, CancellationToken ct)
    {
        var baseSlug = ToSlug(title);
        if (string.IsNullOrEmpty(baseSlug)) baseSlug = "report";

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var candidate = $"{baseSlug}-{Guid.NewGuid().ToString("N")[..6]}";
            var taken = await db.Reports
                .IgnoreQueryFilters()
                .AnyAsync(r => r.Slug == candidate, ct);
            if (!taken) return candidate;
        }
        return $"report-{Guid.NewGuid():N}"[..24];
    }

    private static string ToSlug(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var sb = new System.Text.StringBuilder(input.Length);
        foreach (var c in input.Trim().Normalize(System.Text.NormalizationForm.FormC))
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else sb.Append('-');
        }
        var slug = sb.ToString().ToLower(System.Globalization.CultureInfo.InvariantCulture);
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        if (slug.Length > 80) slug = slug[..80].TrimEnd('-');
        return slug;
    }

    private static int? TryReadInt(string? raw, string key)
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

    /// <summary>
    /// Copy the AI summarize output into <c>report_ai_contents</c> — same
    /// shape <c>ReportAiService.CopySummaryFromResultAsync</c> uses, just
    /// reading the AI service's <c>ai_jobs.OutputData</c> directly here so
    /// we don't have to re-call /reports/summarize for the bulk path.
    /// </summary>
    private static async Task CopySummaryToContentAsync(
        TaqreerkDbContext db, Report report, Guid jobId, string? rawOutput, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawOutput)) return;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(rawOutput); }
        catch (JsonException) { return; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

            // Summary is now a 3-7 item string array (not a paragraph). We
            // re-serialize whatever the AI service emitted into jsonb form
            // for the Summary column. Skip the row entirely if the array is
            // missing or empty — there's nothing meaningful to persist.
            var summaryItems = ExtractStringArray(doc.RootElement, "summary");
            if (summaryItems.Count == 0) return;
            var summary = JsonSerializer.Serialize(summaryItems);

            var keyFindings = JsonSerializer.Serialize(ExtractStringArray(doc.RootElement, "key_findings"));
            var topics      = JsonSerializer.Serialize(ExtractStringArray(doc.RootElement, "topics"));
            var indicators  = ExtractRawJsonArray(doc.RootElement, "indicators");
            var trends      = ExtractRawJsonArray(doc.RootElement, "trends");

            var lang = string.IsNullOrWhiteSpace(report.OriginalLanguage) ? "ar" : report.OriginalLanguage;
            var existing = await db.ReportAiContents
                .FirstOrDefaultAsync(c => c.ReportId == report.Id && c.Language == lang, ct);

            if (existing is null)
            {
                db.ReportAiContents.Add(new ReportAiContent
                {
                    ReportId    = report.Id,
                    Language    = lang,
                    AiJobId     = jobId,
                    Summary     = summary,
                    KeyFindings = keyFindings,
                    Topics      = topics,
                    Indicators  = indicators,
                    Trends      = trends,
                    GeneratedAt = DateTime.UtcNow,
                });
            }
            else
            {
                existing.Summary     = summary;
                existing.KeyFindings = keyFindings;
                existing.Topics      = topics;
                existing.Indicators  = indicators;
                existing.Trends      = trends;
                existing.GeneratedAt = DateTime.UtcNow;
                existing.AiJobId     = jobId;
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

    private static string ExtractRawJsonArray(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array)
            return "[]";
        return el.GetRawText();
    }
}
