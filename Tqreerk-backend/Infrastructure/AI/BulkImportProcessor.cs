using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PDFtoImage;
using Taqreerk.Application.Common;
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

    /// <summary>Upload pipeline flush size. After every N successful uploads
    /// we immediately dispatch /bulk/ingest for that mini-batch so GPU
    /// processing begins while the remaining PDFs are still being fetched
    /// and uploaded — overlapping upload and AI stages rather than waiting
    /// for all uploads to finish first.
    /// Matches the number of GPU instances (6) so each flush saturates the
    /// full GPU fleet in one /bulk/ingest call.</summary>
    private const int IngestFlushSize = 6;

    /// <summary>Maximum number of upload batches to process in a single tick.
    /// Each batch = <see cref="IngestFlushSize"/> items uploaded in parallel.
    /// 5 batches × 6 items = 30 items per upload tick. The advance loop runs
    /// in a separate Task so AdvanceInFlightJobAsync still fires every 10 s
    /// regardless of how long these uploads take.</summary>
    private const int MaxPendingBatchesPerTick = 5;

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

        // Two independent loops so the fast "advance" path (Ingesting →
        // Summarizing → Completed, just DB reads + short HTTP calls to the AI
        // service) is NEVER blocked by the slow "upload" path (PDF downloads
        // from external servers, GCS writes — can take 5-25 min per batch).
        //
        // Before this split, AdvanceInFlightJobAsync ran in the same tick as
        // ProcessPendingItemsAsync. Because ProcessPendingItemsAsync was awaited
        // first and could block for up to 25 minutes (MaxPendingBatchesPerTick=5
        // × 3 items × 5-min per-item timeout), completed GPU ingest jobs sat in
        // Ingesting with no summarize dispatched for the entire upload duration.
        // That's why summarization stopped while ingest kept running.
        await Task.WhenAll(
            RunAdvanceLoopAsync(stoppingToken),
            RunUploadLoopAsync(stoppingToken)
        );

        _logger.LogInformation("BulkImportProcessor stopped");
    }

    /// <summary>
    /// Fast loop: advances in-flight jobs through Ingesting → Summarizing →
    /// Completed. Fires every <see cref="PollInterval"/> (10 s) without
    /// exception — PDF download speed never delays it.
    /// </summary>
    private async Task RunAdvanceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await AdvanceTickAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BulkImportProcessor advance-tick failed");
            }

            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Slow loop: downloads PDFs, uploads to GCS, and dispatches /bulk/ingest.
    /// Each tick can take 5-25 min for large batches; that never delays the
    /// advance loop because the two loops run via <see cref="Task.WhenAll"/>.
    /// </summary>
    private async Task RunUploadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await UploadTickAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BulkImportProcessor upload-tick failed");
            }

            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Advance tick: for every Processing job, polls ai_jobs for completed
    /// Ingestion/Summarization work and advances items to the next stage.
    /// Fast — only indexed DB reads and short HTTP calls; no file I/O.
    /// </summary>
    private async Task AdvanceTickAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaqreerkDbContext>();
        var ai = scope.ServiceProvider.GetRequiredService<IAiServiceClient>();

        var inFlightJobs = await db.BulkImportJobs
            .Where(j => j.Status == BulkImportStatus.Processing)
            .Include(j => j.Items)
            .ToListAsync(ct);

        foreach (var job in inFlightJobs)
        {
            await AdvanceInFlightJobAsync(db, ai, job, ct);
        }
    }

    /// <summary>How long an item may stay in Uploading before it is considered
    /// orphaned and reset to Pending. Items get stuck in Uploading when the
    /// worker restarts mid-download (Cloud Run deploy, scale event, OOM).
    /// The new instance's upload tick only looks at Pending rows, so without
    /// this reset the orphaned row would stay stuck forever.
    /// 15 min is comfortably above the per-item 5-min body-read timeout plus
    /// the GCS write time for the largest PDFs we handle (≤ 200 MB).</summary>
    private static readonly TimeSpan StuckUploadTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Upload tick: processes newly-queued jobs and retry-driven Pending items
    /// inside already-Processing jobs. Slow — fetches PDFs and writes to GCS.
    /// </summary>
    private async Task UploadTickAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaqreerkDbContext>();
        var files = scope.ServiceProvider.GetRequiredService<IFileStorage>();
        var ai = scope.ServiceProvider.GetRequiredService<IAiServiceClient>();
        var storageOpts = scope.ServiceProvider.GetRequiredService<IOptions<FileStorageSettings>>().Value;

        // 0. Stuck-upload recovery. When the worker restarts mid-download the
        //    item stays in Uploading forever because ProcessPendingItemsAsync
        //    only queries Pending rows. Reset any item that has been Uploading
        //    for longer than StuckUploadTimeout back to Pending so the queries
        //    below pick it up and retry it.
        var stuckCutoff = DateTime.UtcNow - StuckUploadTimeout;
        var stuckCount = await db.BulkImportItems
            .Where(i => i.Stage == BulkImportItemStage.Uploading
                     && i.StartedAt < stuckCutoff)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Stage, BulkImportItemStage.Pending)
                .SetProperty(i => i.StartedAt, (DateTime?)null), ct);
        if (stuckCount > 0)
            _logger.LogWarning(
                "[bulk-import] reset {Count} stuck-Uploading item(s) back to Pending " +
                "(StartedAt < {Cutoff:u})", stuckCount, stuckCutoff);

        // 1. Newly-queued jobs — kick off the upload stage for each pending item.
        var pendingJobs = await db.BulkImportJobs
            .Where(j => j.Status == BulkImportStatus.Pending)
            .Include(j => j.Items)
            .ToListAsync(ct);
        foreach (var job in pendingJobs)
        {
            await ProcessPendingJobAsync(db, files, ai, storageOpts, job, ct);
        }

        // 2. Retry-driven Pending items inside already-Processing jobs. Items
        //    land here after /retry-failed rolls Failed rows back to Pending
        //    without flipping the job's own Status. Also picks up any items
        //    just reset from Uploading above (their job is already Processing).
        //    The advance loop cannot handle these because they have no
        //    IngestJobId to poll yet — they need the full upload + ingest
        //    dispatch path first.
        var inFlightJobsWithPending = await db.BulkImportJobs
            .Where(j => j.Status == BulkImportStatus.Processing
                     && j.Items.Any(i => i.Stage == BulkImportItemStage.Pending))
            .Include(j => j.Items)
            .ToListAsync(ct);
        foreach (var job in inFlightJobsWithPending)
        {
            await ProcessPendingItemsAsync(db, files, ai, storageOpts, job, ct);
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
    /// upload step and go straight to ingest.
    ///
    /// Pipelined: every <see cref="IngestFlushSize"/> successful uploads we
    /// immediately call /bulk/ingest for that mini-batch so GPU processing
    /// starts while the remaining PDFs are still being downloaded and
    /// uploaded — upload and AI stages overlap instead of running fully
    /// sequential.
    /// </summary>
    private async Task ProcessPendingItemsAsync(
        TaqreerkDbContext db,
        IFileStorage files,
        IAiServiceClient ai,
        FileStorageSettings storage,
        BulkImportJob job,
        CancellationToken ct)
    {
        // Atomically claim pending items — prevents duplicate processing when
        // multiple backend instances run concurrently (Cloud Run autoscaling).
        // FOR UPDATE SKIP LOCKED makes this a work-queue: each row is claimed
        // by exactly one instance; other instances skip it and move on.
        var limit = MaxPendingBatchesPerTick * IngestFlushSize;
        var claimedItems = await db.BulkImportItems
            .FromSqlInterpolated($@"
                UPDATE bulk_import_items
                SET ""Stage"" = 'Uploading', ""StartedAt"" = NOW()
                WHERE ""Id"" IN (
                    SELECT ""Id"" FROM bulk_import_items
                    WHERE ""JobId"" = {job.Id}
                      AND ""Stage"" = 'Pending'
                    ORDER BY ""RowIndex""
                    LIMIT {limit}
                    FOR UPDATE SKIP LOCKED
                )
                RETURNING *")
            .AsNoTracking()
            .ToListAsync(ct);

        if (claimedItems.Count == 0) return;

        foreach (var item in claimedItems)
            db.Entry(item).State = EntityState.Detached;

        await Task.WhenAll(claimedItems.Select(item =>
            UploadItemInNewScopeAsync(item, job.OrganizationId, job.CreatedByUserId, ct)));

        // Re-query all items fresh — each upload committed in its own scope.
        var allIds = claimedItems.Select(i => i.Id).ToHashSet();
        var freshItems = await db.BulkImportItems
            .Where(i => allIds.Contains(i.Id))
            .Include(i => i.Report)
            .ToListAsync(ct);

        int newlyFailed = 0;
        foreach (var fresh in freshItems)
        {
            var mem = job.Items.FirstOrDefault(i => i.Id == fresh.Id);
            if (mem == null) continue;
            mem.Stage        = fresh.Stage;
            mem.ReportId     = fresh.ReportId;
            mem.Report       = fresh.Report;
            mem.ErrorMessage = fresh.ErrorMessage;
            mem.StartedAt    = fresh.StartedAt;
            mem.CompletedAt  = fresh.CompletedAt;
            if (fresh.Stage == BulkImportItemStage.Failed) newlyFailed++;
        }

        await db.Entry(job).ReloadAsync(ct);
        if (newlyFailed > 0)
        {
            job.FailedCount += newlyFailed;
            await db.SaveChangesAsync(ct);
        }

        // Dispatch all successfully uploaded items to the GPU in one call.
        // The AI service's module-level Semaphore(6) ensures at most 6 concurrent
        // trigger_ingest connections — excess triggers queue and fire as slots free up.
        var uploadedItems = freshItems
            .Where(i => i.Stage == BulkImportItemStage.Uploading)
            .ToList();
        if (uploadedItems.Count > 0)
            await FlushBatchToIngestAsync(db, ai, storage, job, uploadedItems, ct);

        await FinaliseJobIfDoneAsync(db, job, ct);
    }

    /// <summary>
    /// Dispatch /bulk/ingest for a mini-batch of successfully uploaded items
    /// and advance them to Ingesting. Called mid-loop as a pipeline flush and
    /// again for any tail items at the end of <see cref="ProcessPendingItemsAsync"/>.
    /// </summary>
    private async Task FlushBatchToIngestAsync(
        TaqreerkDbContext db,
        IAiServiceClient ai,
        FileStorageSettings storage,
        BulkImportJob job,
        List<BulkImportItem> batch,
        CancellationToken ct)
    {
        if (batch.Count == 0) return;

        // Make sure every row has its Report nav loaded — the retry path
        // (item.ReportId already set) skips UploadItemAsync, which normally
        // hydrates it, so we lazy-fill any missing ones here.
        var missingReport = batch.Where(i => i.Report is null).ToList();
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
            var ingestItems = batch
                .Where(i => i.ReportId.HasValue)
                .Select(i => new BulkIngestItem(
                    i.ReportId!.Value,
                    ToGcsUri(i.Report?.FileUrl, storage)))
                .ToList();
            if (ingestItems.Count == 0) return;

            var jobs = await ai.BulkIngestAsync(ingestItems, ct);

            // Map AI-side job_id back onto our items. The AI side returns
            // entries keyed by report_id, so we match on that.
            var byReport = jobs.ToDictionary(j => j.ReportId, j => j.JobId);
            foreach (var item in batch)
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
        }
        catch (Exception ex)
        {
            // Whole mini-batch failure on the AI side — fail every item so
            // nothing stays stuck in Uploading.
            _logger.LogError(ex,
                "[bulk-import] /bulk/ingest batch ({Count} items) failed for job={JobId}",
                batch.Count, job.Id);
            foreach (var item in batch)
            {
                MarkItemFailed(item, $"تعذّر بدء معالجة الذكاء الاصطناعي: {ex.Message}");
                job.FailedCount++;
            }
        }

        await db.SaveChangesAsync(ct);
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
            // Per-item body-read timeout (5 min). HttpClient.Timeout covers only
            // the header phase when ResponseHeadersRead is used — after headers
            // arrive the body stream is otherwise unbounded. Some Saudi charity
            // servers stream at < 100 KB/s, so a 35 MB PDF takes 5-11 minutes and
            // a 70 MB PDF can drop the connection mid-stream. Without this cap the
            // whole batch stalls for the duration of the slowest item.
            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            downloadCts.CancelAfter(TimeSpan.FromMinutes(5));
            var dlToken = downloadCts.Token;

            using var http = _httpFactory.CreateClient(HttpClientName);
            using var resp = await http.GetAsync(item.FileUrl, HttpCompletionOption.ResponseHeadersRead, dlToken);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"تعذّر تحميل الملف من الرابط (HTTP {(int)resp.StatusCode}).");

            // Refuse > MaxFetchBytes early when the server reports a length;
            // for chunked transfers we still cap at the read loop below.
            var declaredLength = resp.Content.Headers.ContentLength;
            if (declaredLength is > MaxFetchBytes)
                throw new InvalidOperationException(
                    $"حجم الملف ({declaredLength / 1024 / 1024} MB) يتجاوز الحد المسموح به.");

            await using var src = await resp.Content.ReadAsStreamAsync(dlToken);
            using var ms = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int read;
            long total = 0;
            while ((read = await src.ReadAsync(buffer.AsMemory(), dlToken)) > 0)
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
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Body-read timeout fired (downloadCts), not a shutdown signal.
            throw new InvalidOperationException(
                "انتهت مهلة تحميل الملف (5 دقائق) — الخادم بطيء جداً أو الاتصال متقطع.");
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

        var keywordsRaw = BulkImportKeywordsCache.Get(item.JobId, item.RowIndex);
        foreach (var kw in ReportKeywordHelper.ParseCommaSeparated(keywordsRaw))
        {
            report.Keywords.Add(new ReportKeyword
            {
                Keyword = kw,
                Language = report.OriginalLanguage,
            });
        }

        db.Reports.Add(report);
        await db.SaveChangesAsync(ct);

        item.ReportId = report.Id;
        item.Report = report;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Upload a single pending item in its own DI scope + DbContext so
    /// <see cref="ProcessPendingItemsAsync"/> can run a full batch concurrently
    /// with <see cref="Task.WhenAll"/> without sharing a non-thread-safe
    /// <see cref="TaqreerkDbContext"/>.
    ///
    /// All state changes (Stage, ReportId, ErrorMessage) are persisted inside
    /// this scope's transaction. The caller re-queries the outer db after
    /// <see cref="Task.WhenAll"/> completes to get fresh item + job values.
    /// </summary>
    private async Task UploadItemInNewScopeAsync(
        BulkImportItem item,
        Guid orgId,
        Guid uploaderUserId,
        CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var scopeDb    = scope.ServiceProvider.GetRequiredService<TaqreerkDbContext>();
        var scopeFiles = scope.ServiceProvider.GetRequiredService<IFileStorage>();

        // Resume path: item already has a Report row from a prior attempt.
        // Check whether report_chunks exist to decide where to resume:
        //   • chunks exist → ingest already succeeded; go straight to summarize
        //     (sets Stage=Ingesting with no IngestJobId so AdvanceInFlightJobAsync
        //     dispatches /bulk/summarize on the next tick — skips GPU re-ingest)
        //   • no chunks → ingest never finished; re-run it (Stage=Uploading)
        if (item.ReportId.HasValue)
        {
            var hasChunks = await scopeDb.ReportChunks
                .AnyAsync(c => c.ReportId == item.ReportId.Value, ct);

            if (hasChunks)
            {
                // Chunks are fine — only summarization failed. Park at Ingesting
                // (no IngestJobId) so the processor skips GPU and re-summarizes.
                _logger.LogInformation(
                    "[bulk-import] item={ItemId} report={ReportId} has chunks — resuming at summarize",
                    item.Id, item.ReportId);
                await scopeDb.BulkImportItems
                    .Where(i => i.Id == item.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(i => i.Stage, BulkImportItemStage.Ingesting)
                        .SetProperty(i => i.IngestJobId, (Guid?)null)
                        .SetProperty(i => i.SummarizeJobId, (Guid?)null)
                        .SetProperty(i => i.ErrorMessage, (string?)null)
                        .SetProperty(i => i.StartedAt, DateTime.UtcNow), ct);
            }
            else
            {
                // No chunks — ingest never finished. Re-run GPU pipeline.
                await scopeDb.BulkImportItems
                    .Where(i => i.Id == item.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(i => i.Stage, BulkImportItemStage.Uploading)
                        .SetProperty(i => i.StartedAt, DateTime.UtcNow), ct);
            }
            return;
        }

        try
        {
            // Reload the item in this scope so EF can track it cleanly.
            var trackedItem = await scopeDb.BulkImportItems
                .FirstAsync(i => i.Id == item.Id, ct);
            await UploadItemAsync(scopeDb, scopeFiles, trackedItem, orgId, uploaderUserId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[bulk-import] item={ItemId} upload failed: {Msg}", item.Id, ex.Message);
            var msg = (ex.Message ?? string.Empty) is { Length: > 4000 } m
                ? m[..4000] : ex.Message ?? string.Empty;
            await scopeDb.BulkImportItems
                .Where(i => i.Id == item.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.Stage, BulkImportItemStage.Failed)
                    .SetProperty(i => i.ErrorMessage, msg)
                    .SetProperty(i => i.CompletedAt, DateTime.UtcNow), ct);
        }
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
                // Group by ReportId before dispatching: two items can share a
                // ReportId when the resume-from-retry path and a freshly-
                // completed ingest path both land in readyForSummarize on the
                // same tick. Sending duplicate IDs to BulkSummarizeAsync makes
                // the AI service return two rows for the same report, causing
                // ToDictionary to throw "same key already added". Send each
                // ReportId once; assign the returned SummarizeJobId to ALL items
                // that share that report so none get left behind.
                var byReportId = readyForSummarize
                    .Where(i => i.ReportId.HasValue)
                    .GroupBy(i => i.ReportId!.Value)
                    .ToDictionary(g => g.Key, g => g.ToList());

                try
                {
                    var ids = byReportId.Keys.ToList();
                    var summarizeJobs = await ai.BulkSummarizeAsync(ids, ct);
                    var jobByReport = summarizeJobs.ToDictionary(j => j.ReportId, j => j.JobId);
                    foreach (var (reportId, items) in byReportId)
                    {
                        if (jobByReport.TryGetValue(reportId, out var sjId))
                        {
                            foreach (var item in items)
                            {
                                item.SummarizeJobId = sjId;
                                item.Stage = BulkImportItemStage.Summarizing;
                            }
                        }
                        else
                        {
                            foreach (var item in items)
                            {
                                MarkItemFailed(item, "AI service did not accept the summarize request.");
                                job.FailedCount++;
                            }
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
            var summarizeIds = summarizing.Select(i => i.SummarizeJobId!.Value).Distinct().ToList();
            var statuses = await db.AiJobs
                .Where(j => summarizeIds.Contains(j.Id))
                .Select(j => new { j.Id, j.Status, j.ErrorMessage, j.OutputData })
                .ToListAsync(ct);

            var statusById = statuses.ToDictionary(s => s.Id);
            // Guard against duplicate INSERTs into report_ai_contents when
            // multiple BulkImportItems share the same ReportId+SummarizeJobId
            // (resume-from-retry path, or parallel uploads dispatching the same
            // report twice). CopySummaryToContentAsync checks DB for an existing
            // row, but the check happens before SaveChanges so a second call
            // within the same tick also sees null → two INSERTs → 23505 unique
            // constraint violation → SaveChanges rolls back → report stays
            // unpublished. Tracking which ReportIds we've already copied in this
            // pass means CopySummaryToContentAsync is called at most once per
            // report per tick; all items sharing that report still get their
            // stage/counter updated correctly.
            var copiedReportIds = new HashSet<Guid>();
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
                    if (copiedReportIds.Add(rid))
                        await CopySummaryToContentAsync(db, report, item.SummarizeJobId.Value, st.OutputData, ct);
                    report.Status = ReportStatus.Published;
                    report.PublishedAt ??= DateTime.UtcNow;

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
        BulkImportKeywordsCache.ClearJob(job.Id);

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

        var lang = string.IsNullOrWhiteSpace(item.OriginalLanguage) ? "ar" : item.OriginalLanguage!;
        await ReportKeywordHelper.ReplaceAsync(
            db,
            existingReportId,
            lang,
            ReportKeywordHelper.ParseCommaSeparated(
                BulkImportKeywordsCache.Get(item.JobId, item.RowIndex)),
            ct);

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
            // Bump the job's success counter atomically in the DB — we won't
            // pass through any later branch that would normally increment it.
            // ExecuteUpdateAsync works whether the Job nav-property is loaded
            // or not (parallel upload tasks don't eagerly load it).
            await db.BulkImportJobs
                .Where(j => j.Id == item.JobId)
                .ExecuteUpdateAsync(s => s.SetProperty(
                    j => j.CompletedCount, j => j.CompletedCount + 1), ct);
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
                    GeneratedAt = DateTime.UtcNow,
                });
            }
            else
            {
                existing.Summary     = summary;
                existing.KeyFindings = keyFindings;
                existing.Topics      = topics;
                existing.Indicators  = indicators;
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
