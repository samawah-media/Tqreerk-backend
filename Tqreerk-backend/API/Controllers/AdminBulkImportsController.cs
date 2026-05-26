using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.API.Authorization;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Services;

namespace Taqreerk.API.Controllers;

/// <summary>
/// Staff-only "bulk import third-party reports from an Excel sheet"
/// endpoints. The controller is intentionally thin — parse + queue lives
/// in <see cref="IBulkImportService"/>, the actual fetch + AI pipeline
/// runs in <c>BulkImportProcessor</c> background service.
/// </summary>
[ApiController]
[Route("api/admin/bulk-imports")]
[Produces("application/json")]
[Authorize]
[RequirePlatformStaff]
public class AdminBulkImportsController : ControllerBase
{
    /// <summary>200 MB headroom — a 1000-row bulk-import sheet with text-only
    /// columns is typically under 5 MB, but Excel files balloon with embedded
    /// styles/RTL formatting so we keep a generous buffer. Raised from 50 MB
    /// to accommodate larger sheets (e.g. 101 MB uploads).</summary>
    private const long MaxExcelBytes = 200L * 1024 * 1024;

    private readonly IBulkImportService _bulk;

    public AdminBulkImportsController(IBulkImportService bulk)
    {
        _bulk = bulk;
    }

    /// <summary>Upload an Excel sheet describing up to 1000 third-party
    /// reports. The endpoint parses + validates synchronously, then hands
    /// off to the background processor; clients poll
    /// <c>GET /api/admin/bulk-imports/{id}</c> for live progress.</summary>
    [HttpPost]
    [RequestSizeLimit(MaxExcelBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxExcelBytes)]
    [ProducesResponseType(typeof(JobCreatedResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "ملف Excel مطلوب." });
        if (file.Length > MaxExcelBytes)
            return BadRequest(new { error = "حجم الملف يتجاوز الحد المسموح به." });

        // Cheap content-type sanity check — ClosedXML throws on non-xlsx
        // anyway, but a friendly message beats the raw library error.
        var name = file.FileName?.ToLowerInvariant() ?? string.Empty;
        if (!name.EndsWith(".xlsx") && !name.EndsWith(".xlsm"))
            return BadRequest(new { error = "صيغة الملف غير مدعومة — يجب أن يكون .xlsx" });

        await using var stream = file.OpenReadStream();
        try
        {
            var jobId = await _bulk.CreateJobAsync(userId, stream, file.FileName, ct);
            return Accepted(new JobCreatedResponse(jobId));
        }
        catch (InvalidOperationException ex)
        {
            // Parse / validation errors are 400 — the caller knows how to
            // fix them.
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Live progress for one bulk-import job — polled every few
    /// seconds by the admin UI while items are still in flight.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(BulkImportJobDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var job = await _bulk.GetJobAsync(id, ct);
        if (job is null) return NotFound();
        return Ok(job);
    }

    /// <summary>List the calling staff member's recent bulk imports
    /// (newest first, lean payload — items not included).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<BulkImportJobSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] int take = 20, CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _bulk.ListJobsAsync(userId, take, ct));
    }

    /// <summary>Download the empty Excel template so the admin sees the
    /// exact column headers the parser expects.</summary>
    [HttpGet("template")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult DownloadTemplate()
    {
        var bytes = _bulk.GenerateTemplate();
        return File(bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "bulk-import-template.xlsx");
    }

    /// <summary>Reset every Failed row on the job back to Pending so the
    /// background processor takes another swing at them. The job itself
    /// flips back to Processing if it had already reached a terminal
    /// state. Returns the number of items that were eligible for retry
    /// (zero is a no-op success, not an error).</summary>
    [HttpPost("{id:guid}/retry-failed")]
    [ProducesResponseType(typeof(RetryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RetryFailed(Guid id, CancellationToken ct)
    {
        try
        {
            var count = await _bulk.RetryFailedAsync(id, ct);
            return Ok(new RetryResponse(count));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }

    public record JobCreatedResponse(Guid JobId);

    /// <summary>Number of items that were eligible for retry. The frontend
    /// uses this to decide between a "بدأت إعادة المحاولة لـ N صف" toast
    /// and a "لا توجد صفوف فاشلة" hint when the count is zero.</summary>
    public record RetryResponse(int RetriedCount);
}
