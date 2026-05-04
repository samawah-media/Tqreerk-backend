using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.Application.DTOs.Analytics;
using Taqreerk.Application.DTOs.Dashboard;
using Taqreerk.Application.DTOs.Organizations;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

[ApiController]
[Route("api/organizations")]
[Produces("application/json")]
[Authorize]
public class OrganizationsController : ControllerBase
{
    private const long MaxUploadBytes = 5 * 1024 * 1024;

    private readonly IOrganizationService _orgs;
    private readonly IDashboardService _dashboard;
    private readonly IOrganizationAnalyticsService _analytics;

    public OrganizationsController(
        IOrganizationService orgs,
        IDashboardService dashboard,
        IOrganizationAnalyticsService analytics)
    {
        _orgs = orgs;
        _dashboard = dashboard;
        _analytics = analytics;
    }

    /// <summary>Returns the organization the current user is a member of, with profile + files.</summary>
    [HttpGet("me")]
    [ProducesResponseType(typeof(OrganizationDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMine(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _orgs.GetMineAsync(userId, ct));
    }

    /// <summary>Wizard step 1 — basic info (country, city, phone, CR number).</summary>
    [HttpPatch("me/basics")]
    [ProducesResponseType(typeof(OrganizationDetailDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateBasics([FromBody] UpdateOrganizationBasicsRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _orgs.UpdateBasicsAsync(userId, req, ct));
    }

    /// <summary>Wizard step 2 — type, sector, website, description.</summary>
    [HttpPatch("me/scope")]
    [ProducesResponseType(typeof(OrganizationDetailDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateScope([FromBody] UpdateOrganizationScopeRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _orgs.UpdateScopeAsync(userId, req, ct));
    }

    /// <summary>Wizard step 3 — reports authoring intent.</summary>
    [HttpPatch("me/reports")]
    [ProducesResponseType(typeof(OrganizationDetailDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateReports([FromBody] UpdateOrganizationReportsRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _orgs.UpdateReportsAsync(userId, req, ct));
    }

    /// <summary>Wizard step 4 — contact person + policies acceptance.</summary>
    [HttpPatch("me/contact")]
    [ProducesResponseType(typeof(OrganizationDetailDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateContact([FromBody] UpdateOrganizationContactRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _orgs.UpdateContactAsync(userId, req, ct));
    }

    /// <summary>Upload a file for the organization (commercial register, logo, etc.). 5 MB limit; PDF/JPG/PNG.</summary>
    [HttpPost("me/files")]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(OrganizationFileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadFile([FromForm] OrganizationFileUploadForm form, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        if (form.File is null || form.File.Length == 0)
            return BadRequest(new { title = "File is required." });
        if (string.IsNullOrWhiteSpace(form.FileType))
            return BadRequest(new { title = "File type is required." });

        await using var stream = form.File.OpenReadStream();
        var dto = await _orgs.UploadFileAsync(userId, form.FileType, stream, form.File.FileName, form.File.ContentType, ct);
        return Ok(dto);
    }

    /// Wraps the multipart form so Swashbuckle can build a single schema for the
    /// upload endpoint. Mixing [FromForm] IFormFile with separate scalar [FromForm]
    /// parameters trips Swashbuckle's parameter-binding inspection.
    public class OrganizationFileUploadForm
    {
        public IFormFile? File { get; set; }
        public string FileType { get; set; } = string.Empty;
    }

    // ── Dashboard ────────────────────────────────────────────────────────────

    /// <summary>Top-line KPIs for the org dashboard. 5-minute server cache.</summary>
    [HttpGet("me/stats")]
    [ProducesResponseType(typeof(OrganizationStatsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStats(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _dashboard.GetOrganizationStatsAsync(userId, ct));
    }

    /// <summary>Most recent audit-log entries for the org. Default 10, max 50.</summary>
    [HttpGet("me/recent-activity")]
    [ProducesResponseType(typeof(IReadOnlyList<RecentActivityDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRecentActivity([FromQuery] int take = 10, CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _dashboard.GetRecentActivityAsync(userId, take, ct));
    }

    // ── Analytics ────────────────────────────────────────────────────────────

    /// <summary>Aggregated analytics for the caller's org over [from, to].
    /// Defaults to the trailing 30 days if either bound is omitted.</summary>
    [HttpGet("me/analytics")]
    [ProducesResponseType(typeof(OrganizationAnalyticsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAnalytics(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var toDate = (to ?? DateTime.UtcNow.Date);
        var fromDate = (from ?? toDate.AddDays(-29));
        return Ok(await _analytics.GetOrganizationAnalyticsAsync(userId, fromDate, toDate, ct));
    }

    // ── Members ──────────────────────────────────────────────────────────────

    /// <summary>List active members of the current user's organization.</summary>
    [HttpGet("me/members")]
    [ProducesResponseType(typeof(IReadOnlyList<OrganizationMemberDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMembers(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _orgs.ListMembersAsync(userId, ct));
    }

    /// <summary>Remove a member (soft). The founder cannot be removed; the last member cannot be removed.</summary>
    [HttpDelete("me/members/{targetUserId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveMember(Guid targetUserId, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        await _orgs.RemoveMemberAsync(userId, targetUserId, ip, ct);
        return NoContent();
    }

    /// <summary>Change a member's role. Founder-only. The founder's own role is immutable.</summary>
    [HttpPatch("me/members/{targetUserId:guid}/role")]
    [ProducesResponseType(typeof(OrganizationMemberDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ChangeMemberRole(
        Guid targetUserId, [FromBody] ChangeMemberRoleRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var dto = await _orgs.ChangeMemberRoleAsync(userId, targetUserId, req.RoleName, ip, ct);
        return Ok(dto);
    }

    // ── Invitations ──────────────────────────────────────────────────────────

    /// <summary>Pending invitations for the current org.</summary>
    [HttpGet("me/invitations")]
    [ProducesResponseType(typeof(IReadOnlyList<OrganizationInvitationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetInvitations(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _orgs.ListInvitationsAsync(userId, ct));
    }

    /// <summary>Invite an email address to join the current org. Sends an email with a 7-day link.</summary>
    [HttpPost("me/invitations")]
    [ProducesResponseType(typeof(OrganizationInvitationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateInvitation([FromBody] CreateInvitationRequest req, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        return Ok(await _orgs.CreateInvitationAsync(userId, req.Email, ip, ct));
    }

    /// <summary>Cancel a pending invitation.</summary>
    [HttpDelete("me/invitations/{invitationId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CancelInvitation(Guid invitationId, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        await _orgs.CancelInvitationAsync(userId, invitationId, ip, ct);
        return NoContent();
    }

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }
}
