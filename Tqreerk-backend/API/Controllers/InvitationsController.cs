using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.Application.DTOs.Organizations;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

[ApiController]
[Route("api/invitations")]
[Produces("application/json")]
public class InvitationsController : ControllerBase
{
    private readonly IOrganizationService _orgs;

    public InvitationsController(IOrganizationService orgs) => _orgs = orgs;

    /// <summary>Anonymous preview of an invitation by raw token. Returns org name + status only.</summary>
    [HttpGet("preview")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(InvitationPreviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Preview([FromQuery] string token, CancellationToken ct)
        => Ok(await _orgs.PreviewInvitationAsync(token, ct));

    /// <summary>Authenticated accept. The current user is added to the org if their email matches the invite.</summary>
    [HttpPost("accept")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Accept([FromBody] AcceptInvitationRequest req, CancellationToken ct)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(sub, out var userId)) return Unauthorized();

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        await _orgs.AcceptInvitationAsync(userId, req.Token, ip, ct);
        return NoContent();
    }
}
