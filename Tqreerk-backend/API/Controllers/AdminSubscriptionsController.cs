using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.API.Authorization;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.DTOs.Reports;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Controllers;

[ApiController]
[Route("api/admin/subscriptions")]
[Produces("application/json")]
[Authorize]
[RequirePlatformStaff]
public class AdminSubscriptionsController : ControllerBase
{
    private readonly IAdminSubscriptionsService _subscriptions;

    public AdminSubscriptionsController(IAdminSubscriptionsService subscriptions)
    {
        _subscriptions = subscriptions;
    }

    [HttpGet]
    [RequirePermission("subscriptions:view")]
    [ProducesResponseType(typeof(PagedResult<AdminSubscriptionListItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] AdminSubscriptionsListRequest req, CancellationToken ct)
        => Ok(await _subscriptions.ListAsync(req, ct));

    [HttpGet("{id:guid}")]
    [RequirePermission("subscriptions:view")]
    [ProducesResponseType(typeof(AdminSubscriptionDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        => Ok(await _subscriptions.GetAsync(id, ct));

    [HttpPost("payments/{paymentId:guid}/refund")]
    [RequirePermission("payments:edit")]
    [ProducesResponseType(typeof(RefundSubscriptionPaymentResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RefundPayment(
        Guid paymentId,
        [FromBody] RefundSubscriptionPaymentRequest req,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var actingUserId)) return Unauthorized();
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        return Ok(await _subscriptions.RefundPaymentAsync(
            actingUserId,
            paymentId,
            req,
            ip,
            ct));
    }

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }
}
