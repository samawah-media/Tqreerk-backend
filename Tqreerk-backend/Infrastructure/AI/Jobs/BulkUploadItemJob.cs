using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using PDFtoImage;
using Taqreerk.Application.Common;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Settings;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;
using Taqreerk.Infrastructure.Storage;

namespace Taqreerk.Infrastructure.AI.Jobs;

/// <summary>
/// Hangfire job — Stage 1 of the bulk-import pipeline.
///
/// Downloads the PDF from the Excel-supplied URL, uploads it to GCS,
/// renders a cover image, creates the <see cref="Report"/> row, dispatches
/// <c>/bulk/ingest</c> to the AI service, then enqueues
/// <see cref="BulkAdvanceItemJob"/> to poll for GPU completion.
///
/// Retried up to 3 times (2 min → 10 min → 30 min back-off). On exhaustion
/// <see cref="BulkJobFailedFilter"/> marks the item Failed so the admin can
/// see what happened and optionally retry.
///
/// Idempotent: if the item has already advanced past Uploading (e.g. the
/// instance restarted after the Report row was created but before the Hangfire
/// completion was recorded) the job skips without re-doing work.
/// </summary>
[Queue("bulk-upload")]
[AutomaticRetry(Attempts = 3,
    DelaysInSeconds = new[] { 120, 600, 1800 },
    OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public class BulkUploadItemJob(
    TaqreerkDbContext db,
    IFileStorage files,
    IAiServiceClient ai,
    IOptions<FileStorageSettings> storageOpts,
    IHttpClientFactory httpFactory,
    IBackgroundJobClient jobClient,
    ILogger<BulkUploadItemJob> logger)
{
    /// Named HttpClient key registered in ServiceExtensions — kept here
    /// (not on BulkImportProcessor) so the Hangfire jobs don't depend on
    /// the legacy BackgroundService.
    public const string HttpClientName = "BulkImportProcessor";

    private const long MaxFetchBytes = 200L * 1024 * 1024;

    public async Task ExecuteAsync(Guid itemId, CancellationToken ct = default)
    {
        var item = await db.BulkImportItems
            .Include(i => i.Job)
            .FirstOrDefaultAsync(i => i.Id == itemId, ct);

        if (item is null) return;

        switch (item.Stage)
        {
            // Already advanced — don't redo work, but ensure the advance job exists.
            case BulkImportItemStage.Ingesting:
                jobClient.Enqueue<BulkAdvanceItemJob>(j => j.ExecuteAsync(itemId, CancellationToken.None));
                return;
            case BulkImportItemStage.Summarizing:
            case BulkImportItemStage.Completed:
            case BulkImportItemStage.Failed:
                return;
        }

        var orgId      = item.Job.OrganizationId;
        var uploaderId = item.Job.CreatedByUserId;

        // Deduplication: same title + source combination → reuse existing report.
        var normTitle  = (item.Title  ?? string.Empty).Trim();
        var normSource = (item.Source ?? string.Empty).Trim();
        if (normTitle.Length > 0 && normSource.Length > 0)
        {
            var existingReportId = await db.BulkImportItems
                .Where(i => i.Id != item.Id
                         && i.ReportId != null
                         && i.Source == normSource
                         && i.Title.ToLower() == normTitle.ToLower()
                         && i.Stage != BulkImportItemStage.Failed)
                .Select(i => i.ReportId)
                .FirstOrDefaultAsync(ct);
            if (existingReportId is not null)
            {
                await ResumeExistingReportAsync(item, existingReportId.Value, ct);
                return;
            }
        }

        // Resume path: Report row already created in a prior attempt.
        if (item.ReportId.HasValue)
        {
            var hasChunks = await db.ReportChunks
                .AnyAsync(c => c.ReportId == item.ReportId.Value, ct);
            if (hasChunks)
            {
                // Ingest already completed — skip GPU, go straight to summarize.
                await db.BulkImportItems
                    .Where(i => i.Id == item.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(i => i.Stage, BulkImportItemStage.Ingesting)
                        .SetProperty(i => i.IngestJobId, (Guid?)null)
                        .SetProperty(i => i.SummarizeJobId, (Guid?)null)
                        .SetProperty(i => i.ErrorMessage, (string?)null)
                        .SetProperty(i => i.StartedAt, DateTime.UtcNow), ct);
                jobClient.Enqueue<BulkAdvanceItemJob>(j => j.ExecuteAsync(itemId, CancellationToken.None));
                return;
            }

            // No chunks — ingest never finished. The PDF is already in GCS
            // (ReportId set ⇒ upload completed). Re-dispatch the existing file
            // to the GPU without re-downloading or creating a new Report row.
            var fileUrl = await db.Reports
                .Where(r => r.Id == item.ReportId.Value)
                .Select(r => r.FileUrl)
                .FirstOrDefaultAsync(ct);
            var reIngestGcsUri = BulkPipelineStatics.ToGcsUri(fileUrl, storageOpts.Value);
            var reIngestResult = await ai.BulkIngestAsync(
                new List<BulkIngestItem> { new(item.ReportId.Value, reIngestGcsUri) }, ct);
            var reIngestJobId = reIngestResult.FirstOrDefault(j => j.ReportId == item.ReportId.Value)?.JobId;
            if (reIngestJobId is null)
                throw new InvalidOperationException("AI service did not accept the re-ingest request.");
            item.IngestJobId = reIngestJobId;
            item.Stage       = BulkImportItemStage.Ingesting;
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "[bulk-upload] item={ItemId} re-ingesting existing report={ReportId} ingestJob={IngestJobId}",
                itemId, item.ReportId, reIngestJobId);
            jobClient.Enqueue<BulkAdvanceItemJob>(j => j.ExecuteAsync(itemId, CancellationToken.None));
            return;
        }

        // ── Download ──────────────────────────────────────────────────────────
        item.Stage     = BulkImportItemStage.Uploading;
        item.StartedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        byte[]  pdfBytes;
        string  contentType;
        try
        {
            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            downloadCts.CancelAfter(TimeSpan.FromMinutes(5));
            var dlToken = downloadCts.Token;

            using var http = httpFactory.CreateClient(HttpClientName);
            using var resp = await http.GetAsync(
                item.FileUrl, HttpCompletionOption.ResponseHeadersRead, dlToken);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"تعذّر تحميل الملف من الرابط (HTTP {(int)resp.StatusCode}).");

            if (resp.Content.Headers.ContentLength is > MaxFetchBytes)
                throw new InvalidOperationException(
                    $"حجم الملف يتجاوز الحد المسموح به ({MaxFetchBytes / 1024 / 1024} MB).");

            await using var src = await resp.Content.ReadAsStreamAsync(dlToken);
            using var ms        = new MemoryStream();
            var buf = new byte[64 * 1024];
            int   read;
            long  total = 0;
            while ((read = await src.ReadAsync(buf.AsMemory(), dlToken)) > 0)
            {
                total += read;
                if (total > MaxFetchBytes)
                    throw new InvalidOperationException(
                        $"حجم الملف يتجاوز الحد المسموح به ({MaxFetchBytes / 1024 / 1024} MB).");
                ms.Write(buf, 0, read);
            }
            pdfBytes    = ms.ToArray();
            contentType = resp.Content.Headers.ContentType?.MediaType ?? "application/pdf";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "انتهت مهلة تحميل الملف (5 دقائق) — الخادم بطيء أو الاتصال متقطع.");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"تعذّر الوصول للرابط: {ex.Message}", ex);
        }

        if (pdfBytes.Length < 1024)
            throw new InvalidOperationException("الملف فارغ أو تالف.");

        // ── Upload PDF ────────────────────────────────────────────────────────
        var storage     = storageOpts.Value;
        var slug        = await BulkPipelineStatics.GenerateUniqueSlugAsync(db, item.Title, ct);
        var safeFileName = $"{slug}.pdf";

        using var pdfStream = new MemoryStream(pdfBytes);
        var stored = await files.UploadAsync(pdfStream, safeFileName, contentType, $"reports/{orgId}", ct);

        // ── Cover (best-effort) ───────────────────────────────────────────────
        BulkCoverResult? cover = null;
        try { cover = await RenderAndUploadCoverAsync(files, pdfBytes, slug, orgId, ct); }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[bulk-upload] cover rendering failed for item={Id} — proceeding without cover", itemId);
        }

        // ── Sector (auto-create if unknown) ───────────────────────────────────
        Guid? sectorId = null;
        if (!string.IsNullOrWhiteSpace(item.SectorNameAr))
        {
            sectorId = await db.Sectors
                .Where(s => s.NameAr == item.SectorNameAr)
                .Select(s => (Guid?)s.Id)
                .FirstOrDefaultAsync(ct);

            if (sectorId is null)
            {
                var sectorSlugBase = new string(item.SectorNameAr.ToLowerInvariant()
                    .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
                if (string.IsNullOrEmpty(sectorSlugBase)) sectorSlugBase = "sector";

                var newSector = new Sector
                {
                    Id        = Guid.NewGuid(),
                    NameAr    = item.SectorNameAr,
                    NameEn    = item.SectorNameAr,
                    Slug      = $"{sectorSlugBase}-{Guid.NewGuid().ToString("N")[..8]}",
                    IsActive  = true,
                    SortOrder = 0,
                };
                db.Sectors.Add(newSector);
                await db.SaveChangesAsync(ct);
                sectorId = newSector.Id;
            }
        }

        // ── Country ───────────────────────────────────────────────────────────
        Guid? countryId = null;
        if (!string.IsNullOrWhiteSpace(item.CountryNameAr))
            countryId = await db.Countries
                .Where(c => c.NameAr == item.CountryNameAr)
                .Select(c => (Guid?)c.Id)
                .FirstOrDefaultAsync(ct);

        // ── Report row ────────────────────────────────────────────────────────
        var report = new Report
        {
            OrganizationId      = orgId,
            UploadedByUserId    = uploaderId,
            TitleAr             = item.Title,
            TitleEn             = item.TitleEn,
            Slug                = slug,
            ReportType          = item.ReportType,
            Source              = item.Source,
            Authors             = item.Authors,
            OriginalLanguage    = string.IsNullOrWhiteSpace(item.OriginalLanguage) ? "ar" : item.OriginalLanguage!,
            PublicationYear     = item.PublicationYear,
            FileUrl             = stored.ObjectKey,
            CoverImageUrl       = cover?.MediumKey,
            CoverImageBaseKey   = cover?.BaseKey,
            SourceType          = ReportSourceType.Platform,
            Status              = ReportStatus.Approved,
            SectorId            = sectorId,
            CountryId           = countryId,
            SubmittedForReviewAt = DateTime.UtcNow,
        };

        var keywordsRaw = BulkImportKeywordsCache.Get(item.JobId, item.RowIndex);
        foreach (var kw in ReportKeywordHelper.ParseCommaSeparated(keywordsRaw))
            report.Keywords.Add(new ReportKeyword { Keyword = kw, Language = report.OriginalLanguage });

        db.Reports.Add(report);
        await db.SaveChangesAsync(ct);

        item.ReportId = report.Id;
        item.Report   = report;
        await db.SaveChangesAsync(ct);

        // ── Dispatch to GPU ───────────────────────────────────────────────────
        var gcsUri = BulkPipelineStatics.ToGcsUri(report.FileUrl, storage);
        var ingestResult = await ai.BulkIngestAsync(
            new List<BulkIngestItem> { new(report.Id, gcsUri) }, ct);

        var ingestJobId = ingestResult.FirstOrDefault(j => j.ReportId == report.Id)?.JobId;
        if (ingestJobId is null)
            throw new InvalidOperationException("AI service did not accept the ingest request.");

        item.IngestJobId = ingestJobId;
        item.Stage       = BulkImportItemStage.Ingesting;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "[bulk-upload] item={ItemId} → Ingesting ingestJob={IngestJobId}", itemId, ingestJobId);

        jobClient.Enqueue<BulkAdvanceItemJob>(j => j.ExecuteAsync(itemId, CancellationToken.None));
    }

    // ── Resume existing report ────────────────────────────────────────────────

    private async Task ResumeExistingReportAsync(
        BulkImportItem item, Guid existingReportId, CancellationToken ct)
    {
        var hasChunks  = await db.ReportChunks.AnyAsync(c => c.ReportId == existingReportId, ct);
        var hasContent = await db.ReportAiContents.AnyAsync(c => c.ReportId == existingReportId, ct);

        item.ReportId  = existingReportId;
        item.StartedAt = DateTime.UtcNow;

        var lang = string.IsNullOrWhiteSpace(item.OriginalLanguage) ? "ar" : item.OriginalLanguage!;
        await ReportKeywordHelper.ReplaceAsync(db, existingReportId, lang,
            ReportKeywordHelper.ParseCommaSeparated(
                BulkImportKeywordsCache.Get(item.JobId, item.RowIndex)), ct);

        if (hasChunks && hasContent)
        {
            var report = await db.Reports.FirstOrDefaultAsync(r => r.Id == existingReportId, ct);
            if (report is not null && report.Status != ReportStatus.Published)
            {
                report.Status      = ReportStatus.Published;
                report.PublishedAt ??= DateTime.UtcNow;
            }
            item.Stage       = BulkImportItemStage.Completed;
            item.CompletedAt = DateTime.UtcNow;
            item.ErrorMessage = null;
            await db.SaveChangesAsync(ct);
            await db.BulkImportJobs
                .Where(j => j.Id == item.JobId)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.CompletedCount, j => j.CompletedCount + 1), ct);
            await TryFinaliseJobAsync(item.JobId, ct);
            logger.LogInformation(
                "[bulk-upload] item={ItemId} reused complete report={ReportId}", item.Id, existingReportId);
            return;
        }

        if (hasChunks)
        {
            item.Stage        = BulkImportItemStage.Ingesting;
            item.IngestJobId  = null;
            item.SummarizeJobId = null;
            item.ErrorMessage = null;
            await db.SaveChangesAsync(ct);
            jobClient.Enqueue<BulkAdvanceItemJob>(j => j.ExecuteAsync(item.Id, CancellationToken.None));
            return;
        }

        // No chunks — the matched report exists in GCS but ingest never completed.
        // Re-enqueueing BulkUploadItemJob would loop forever: dedup always
        // matches the same report, which still has no chunks. Dispatch ingest
        // directly instead. Share a sibling's active IngestJobId when possible
        // so we don't fire duplicate GPU jobs for the same report.
        var siblingIngestJobId = await db.BulkImportItems
            .Where(i => i.ReportId == existingReportId
                     && i.Stage == BulkImportItemStage.Ingesting
                     && i.IngestJobId != null)
            .Select(i => i.IngestJobId)
            .FirstOrDefaultAsync(ct);

        if (siblingIngestJobId is not null)
        {
            item.IngestJobId = siblingIngestJobId;
        }
        else
        {
            var existingFileUrl = await db.Reports
                .Where(r => r.Id == existingReportId)
                .Select(r => r.FileUrl)
                .FirstOrDefaultAsync(ct);
            var existingGcsUri = BulkPipelineStatics.ToGcsUri(existingFileUrl, storageOpts.Value);
            var ingestResult = await ai.BulkIngestAsync(
                new List<BulkIngestItem> { new(existingReportId, existingGcsUri) }, ct);
            var ingestJobId = ingestResult.FirstOrDefault(j => j.ReportId == existingReportId)?.JobId;
            if (ingestJobId is null)
                throw new InvalidOperationException("AI service did not accept the ingest request for existing report.");
            item.IngestJobId = ingestJobId;
        }

        item.Stage        = BulkImportItemStage.Ingesting;
        item.SummarizeJobId = null;
        item.ErrorMessage = null;
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "[bulk-upload] item={ItemId} re-ingesting existing report={ReportId}", item.Id, existingReportId);
        jobClient.Enqueue<BulkAdvanceItemJob>(j => j.ExecuteAsync(item.Id, CancellationToken.None));
    }

    // ── Cover rendering ───────────────────────────────────────────────────────

    private static async Task<BulkCoverResult?> RenderAndUploadCoverAsync(
        IFileStorage files, byte[] pdfBytes, string slug, Guid orgId, CancellationToken ct)
    {
        var variants = await Task.Run(() =>
        {
            using var pdfStream = new MemoryStream(pdfBytes);
            using var bitmap    = Conversion.ToImage(pdfStream, page: 0);
            return CoverImageVariants.Generate(bitmap);
        }, ct);

        var coverId = Guid.NewGuid().ToString("N");
        var folder  = $"public/covers/{coverId}";

        var thumb  = await files.UploadPublicAsync(new MemoryStream(variants.Thumb),  CoverImageVariants.ThumbName,  CoverImageEncoder.ContentType, folder, ct);
        var medium = await files.UploadPublicAsync(new MemoryStream(variants.Medium), CoverImageVariants.MediumName, CoverImageEncoder.ContentType, folder, ct);
        var full   = await files.UploadPublicAsync(new MemoryStream(variants.Full),   CoverImageVariants.FullName,   CoverImageEncoder.ContentType, folder, ct);

        var baseKey = medium.ObjectKey[..medium.ObjectKey.LastIndexOf('/')];
        return new BulkCoverResult(baseKey, medium.ObjectKey);
    }

    // ── Job finalisation ──────────────────────────────────────────────────────

    private async Task TryFinaliseJobAsync(Guid jobId, CancellationToken ct)
    {
        var anyActive = await db.BulkImportItems.AnyAsync(i =>
            i.JobId == jobId
            && i.Stage != BulkImportItemStage.Completed
            && i.Stage != BulkImportItemStage.Failed, ct);
        if (anyActive) return;

        await db.BulkImportJobs
            .Where(j => j.Id == jobId && j.Status == BulkImportStatus.Processing)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, BulkImportStatus.Completed)
                .SetProperty(j => j.CompletedAt, DateTime.UtcNow), ct);
        BulkImportKeywordsCache.ClearJob(jobId);
    }

    private sealed record BulkCoverResult(string BaseKey, string MediumKey);
}
