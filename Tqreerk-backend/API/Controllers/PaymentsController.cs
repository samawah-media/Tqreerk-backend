using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Taqreerk.Application.DTOs.Payments;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Services;

namespace Taqreerk.API.Controllers;

[ApiController]
[Route("api/payments")]
[Produces("application/json")]
public class PaymentsController : ControllerBase
{
    private readonly IPaymentCheckoutService _payments;

    public PaymentsController(IPaymentCheckoutService payments)
    {
        _payments = payments;
    }

    /// <summary>Publishable key for Moyasar.js (no secret).</summary>
    [HttpGet("config")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(MoyasarPublicConfigDto), StatusCodes.Status200OK)]
    public IActionResult Config([FromServices] Microsoft.Extensions.Options.IOptions<Application.Settings.MoyasarSettings> opts)
    {
        var s = opts.Value;
        return Ok(new MoyasarPublicConfigDto(
            s.PublishableKey,
            !string.IsNullOrWhiteSpace(s.PublishableKey)));
    }

    [HttpPost("checkout")]
    [Authorize]
    [ProducesResponseType(typeof(CheckoutSessionDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Checkout(
        [FromBody] CreateCheckoutRequestDto body,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _payments.CreateCheckoutAsync(userId, body.PlanId, ct));
    }

    /// <summary>
    /// Called by the SPA after Moyasar redirect (?id=). Verifies with Moyasar API
    /// and activates the subscription.
    /// </summary>
    [HttpPost("verify")]
    [Authorize]
    [ProducesResponseType(typeof(VerifyPaymentResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Verify(
        [FromBody] VerifyPaymentRequestDto body,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(body.MoyasarPaymentId))
            return BadRequest(new { error = "معرّف الدفع مطلوب." });

        return Ok(await _payments.VerifyAndFulfillAsync(userId, body.MoyasarPaymentId, ct));
    }

    [HttpPost("cancel-auto-renew")]
    [Authorize]
    [ProducesResponseType(typeof(CancelAutoRenewResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> CancelAutoRenew(CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(await _payments.CancelAutoRenewAsync(userId, ct));
    }

    [HttpPost("moyasar/webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> MoyasarWebhook(CancellationToken ct)
    {
        string raw;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            raw = await reader.ReadToEndAsync(ct);

        var signature = Request.Headers["X-Moyasar-Signature"].FirstOrDefault()
            ?? Request.Headers["x-moyasar-signature"].FirstOrDefault();

        if (!_payments.TryVerifyWebhookSignature(raw, signature))
            return Unauthorized();

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var eventType = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";

        if (!root.TryGetProperty("data", out var data))
            return Ok();

        var payment = MoyasarApiClient.ParsePayment(data);
        if (payment is null)
            return Ok();

        await _payments.HandleWebhookAsync(eventType, payment, ct);
        return Ok();
    }

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }
}
