using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.Application.DTOs.Analytics;
using Taqreerk.Application.DTOs.FeatureRequests;
using Taqreerk.Application.DTOs.Reports;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

[ApiController]
[Route("api/reports")]
[Produces("application/json")]
[Authorize]
public class ReportsController : ControllerBase
{
    private const long MaxUploadBytes = 200L * 1024 * 1024; // 200 MB

    private readonly IReportService _reports;
    private readonly IReportAiService _ai;
    private readonly IOrganizationAnalyticsService _analytics;
    private readonly IFeatureRequestsService _featureRequests;
    private readonly ILogger<ReportsController> _logger;

    public ReportsController(
        IReportService reports,
        IReportAiService ai,
        IOrganizationAnalyticsService analytics,
        IFeatureRequestsService featureRequests,
        ILogger<ReportsController> logger)
    {
        _reports = reports;
        _ai = ai;
        _analytics = analytics;
        _featureRequests = featureRequests;
        _logger = logger;
    }

    /// <summary>List the caller's organization's reports (paginated).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<ReportListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? q = null,
        CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await _reports.ListMineAsync(userId, page, pageSize, q, ct);
        return Ok(result);
    }

    /// <summary>Get a single report. Caller must belong to the owning org.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ReportDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _reports.GetAsync(userId, id, ct));
    }

    /// <summary>Upload a new report (PDF + metadata). Status starts at Draft.</summary>
    [HttpPost]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(ReportDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromForm] CreateReportForm form, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();

        // Log what the multipart binder actually picked up. If CoverImage stays
        // null in this line while the frontend swears it sent the field, the
        // model-binding key is wrong (e.g. `coverImage` vs `CoverImage`).
        _logger.LogInformation(
            "POST /api/reports — file={FileName} ({FileSize} bytes), coverImage={CoverName} ({CoverSize} bytes), title={Title}",
            form.File?.FileName ?? "(none)",
            form.File?.Length ?? 0,
            form.CoverImage?.FileName ?? "(none)",
            form.CoverImage?.Length ?? 0,
            form.Title);

        if (form.File is null || form.File.Length == 0)
            return BadRequest(new { title = "File is required." });

        DateOnly? publicationDate = null;
        if (!string.IsNullOrWhiteSpace(form.PublicationDate))
        {
            if (!DateOnly.TryParse(form.PublicationDate, out var pd))
                return BadRequest(new { title = "Invalid publicationDate (expected YYYY-MM-DD)." });
            publicationDate = pd;
        }

        var dto = new CreateReportRequest(
            Title: form.Title,
            Description: form.Description,
            ReportType: form.ReportType,
            OriginalLanguage: form.OriginalLanguage,
            PublicationYear: form.PublicationYear,
            PublicationDate: publicationDate,
            SectorId: form.SectorId,
            CountryId: form.CountryId
        );

        // Buffer both files into seekable MemoryStreams before handing them to
        // the service. The Google Cloud Storage SDK occasionally retries chunked
        // uploads on transient failures and needs to rewind the stream to do
        // so. The IFormFile-backed request stream isn't seekable and surfaces
        // as "The inner stream position has changed unexpectedly" on retry.
        // Buffering trades RAM (capped by the 50 MB request limit) for upload
        // robustness — a fair deal for a low-frequency operation.
        await using var reportBuffer = await BufferAsync(form.File, ct);
        var reportFile = new UploadedFile(reportBuffer, form.File.FileName, form.File.ContentType);

        UploadedFile? coverFile = null;
        MemoryStream? coverBuffer = null;
        try
        {
            if (form.CoverImage is not null && form.CoverImage.Length > 0)
            {
                coverBuffer = await BufferAsync(form.CoverImage, ct);
                coverFile = new UploadedFile(coverBuffer, form.CoverImage.FileName, form.CoverImage.ContentType);
            }

            var created = await _reports.CreateAsync(userId, dto, reportFile, coverFile, ct);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("GcsPublicBucketName"))
        {
            _logger.LogError(ex, "Cover upload failed: public GCS bucket not configured.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { title = "خدمة رفع صور الغلاف غير متاحة حالياً — يرجى التواصل مع الدعم." });
        }
        finally
        {
            if (coverBuffer is not null) await coverBuffer.DisposeAsync();
        }
    }

    /// Copy an IFormFile into a seekable MemoryStream positioned at 0. Required
    /// because GCS retry logic seeks the source stream — which the multipart
    /// request body doesn't support.
    private static async Task<MemoryStream> BufferAsync(IFormFile file, CancellationToken ct)
    {
        var ms = new MemoryStream(checked((int)Math.Max(file.Length, 0)));
        await using var src = file.OpenReadStream();
        await src.CopyToAsync(ms, ct);
        ms.Position = 0;
        return ms;
    }

    /// <summary>Soft-delete a report. Caller must belong to the owning org.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        await _reports.DeleteAsync(userId, id, ct);
        return NoContent();
    }

    /// <summary>Per-report analytics drilldown. Same date-window contract
    /// as the org-wide endpoint (`/organizations/me/analytics`) — defaults
    /// to the trailing 30 days. Caller must own the report via org
    /// membership; cross-org probes get a 404.</summary>
    [HttpGet("{id:guid}/analytics")]
    [ProducesResponseType(typeof(ReportAnalyticsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReportAnalytics(
        Guid id,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var toDate = (to ?? DateTime.UtcNow.Date);
        var fromDate = (from ?? toDate.AddDays(-29));
        return Ok(await _analytics.GetReportAnalyticsAsync(userId, id, fromDate, toDate, ct));
    }

    /// <summary>Patch editable metadata (title, description, sector, country,
    /// publication year/date, report type, language). Does not touch the PDF,
    /// AI content, slug or status. Any null field is left unchanged.</summary>
    [HttpPatch("{id:guid}")]
    [ProducesResponseType(typeof(ReportDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateMetadata(
        Guid id, [FromBody] UpdateReportMetadataRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _reports.UpdateMetadataAsync(userId, id, req, ct));
    }

    /// <summary>Submit a "feature this report" request to the platform
    /// admins. Only published, org-owned reports are eligible. 409 when
    /// a Pending request already exists for the same report.</summary>
    [HttpPost("{id:guid}/feature-request")]
    [ProducesResponseType(typeof(FeatureRequestDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateFeatureRequest(
        Guid id, [FromBody] CreateFeatureRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var created = await _featureRequests.CreateAsync(userId, id, req, ct);
        return CreatedAtAction(nameof(GetFeatureRequest), new { id }, created);
    }

    /// <summary>Latest feature request for the given org-owned report
    /// (any status). 200 with the row, or 200 with `null` body when the
    /// report has never been submitted.</summary>
    [HttpGet("{id:guid}/feature-request")]
    [ProducesResponseType(typeof(FeatureRequestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetFeatureRequest(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _featureRequests.GetForReportAsync(userId, id, ct));
    }

    /// <summary>Re-submit a returned-for-edit report with a new PDF (and
    /// optional cover). Status flips back to PendingReview and the report
    /// re-enters the moderation queue.</summary>
    [HttpPost("{id:guid}/resubmit")]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(ReportDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Resubmit(
        Guid id,
        [FromForm] ResubmitReportForm form,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        if (form.File is null || form.File.Length == 0)
            return BadRequest(new { title = "File is required." });

        await using var reportBuffer = await BufferAsync(form.File, ct);
        var reportFile = new UploadedFile(reportBuffer, form.File.FileName, form.File.ContentType);

        UploadedFile? coverFile = null;
        MemoryStream? coverBuffer = null;
        try
        {
            if (form.CoverImage is not null && form.CoverImage.Length > 0)
            {
                coverBuffer = await BufferAsync(form.CoverImage, ct);
                coverFile = new UploadedFile(coverBuffer, form.CoverImage.FileName, form.CoverImage.ContentType);
            }

            var updated = await _reports.ResubmitAsync(userId, id, reportFile, coverFile, ct);
            return Ok(updated);
        }
        finally
        {
            if (coverBuffer is not null) await coverBuffer.DisposeAsync();
        }
    }

    /// <summary>Get the current AI processing status (jobs + summary + translations).
    /// Polled by the frontend every 3s while overall status is Processing.</summary>
    [HttpGet("{id:guid}/ai-status")]
    [ProducesResponseType(typeof(ReportAiStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAiStatus(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _ai.GetStatusAsync(userId, id, ct));
    }

    /// <summary>Re-run failed AI jobs for the report (or kick off the pipeline
    /// from scratch if all jobs already completed). Caller must own the report.</summary>
    [HttpPost("{id:guid}/regenerate-ai")]
    [ProducesResponseType(typeof(ReportAiStatusDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RegenerateAi(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        await _ai.RegenerateAsync(userId, id, ct);
        var status = await _ai.GetStatusAsync(userId, id, ct);
        return Accepted(status);
    }

    /// <summary>
    /// Manually queue a translation pass for the report. Gated by the org's
    /// `Organization.TranslationEnabled` flag (toggled by staff in the admin
    /// app). Returns 202 with the current AI status; clients poll
    /// /reports/{id}/ai/status for completion.
    /// </summary>
    [HttpPost("{id:guid}/ai/translate")]
    [ProducesResponseType(typeof(ReportAiStatusDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> TranslateAi(Guid id, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        await _ai.EnqueueTranslateAsync(userId, id, ct);
        var status = await _ai.GetStatusAsync(userId, id, ct);
        return Accepted(status);
    }

    /// Wraps the multipart form so Swashbuckle can build a single schema for the
    /// upload endpoint. Mixing [FromForm] IFormFile with separate scalar [FromForm]
    /// parameters trips Swashbuckle's parameter-binding inspection.
    public class CreateReportForm
    {
        public IFormFile? File { get; set; }
        /// Optional thumbnail rendered on the report card / detail page. Image
        /// formats only (PNG/JPEG/WEBP, up to 5 MB).
        public IFormFile? CoverImage { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? ReportType { get; set; }
        public string? OriginalLanguage { get; set; }
        public int? PublicationYear { get; set; }
        /// ISO date (YYYY-MM-DD). Parsed in the controller because multipart binding
        /// doesn't handle DateOnly directly across all client toolchains.
        public string? PublicationDate { get; set; }
        public Guid? SectorId { get; set; }
        public Guid? CountryId { get; set; }
    }

    /// Multipart form for resubmitting a returned-for-edit report. Carries
    /// only the file(s) — metadata stays the same, since the org isn't
    /// re-running the wizard, just swapping PDF content.
    public class ResubmitReportForm
    {
        public IFormFile? File { get; set; }
        public IFormFile? CoverImage { get; set; }
    }

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }
}
