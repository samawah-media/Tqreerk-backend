using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<PaymentsController> _logger;

    public PaymentsController(
        IPaymentCheckoutService payments,
        ILogger<PaymentsController> logger)
    {
        _payments = payments;
        _logger = logger;
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
        return Ok(await _payments.CreateCheckoutAsync(
            userId,
            body.PlanId,
            body.CallbackUrl,
            ct));
    }

    /// <summary>
    /// Called from Moyasar form on_completed before redirect — persists card token on the subscription.
    /// </summary>
    [HttpPost("register-card-token")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> RegisterCardToken(
        [FromBody] RegisterCardTokenRequestDto body,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        if (body.PaymentId == Guid.Empty
            || string.IsNullOrWhiteSpace(body.MoyasarPaymentId)
            || string.IsNullOrWhiteSpace(body.SourceToken))
        {
            return BadRequest(new { error = "بيانات التوكن غير مكتملة." });
        }

        var ok = await _payments.RegisterCardTokenAsync(
            userId,
            body.PaymentId,
            body.MoyasarPaymentId,
            body.SourceToken,
            ct);
        return Ok(new { saved = ok });
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

        return Ok(await _payments.VerifyAndFulfillAsync(
            userId,
            body.MoyasarPaymentId,
            body.SourceToken,
            ct));
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
        {
            _logger.LogWarning(
                "Moyasar webhook rejected: invalid or missing signature (hasHeader={HasHeader}).",
                !string.IsNullOrWhiteSpace(signature));
            return Unauthorized();
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        var eventType = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        var eventId = root.TryGetProperty("id", out var eid) ? eid.GetString() : null;

        if (!root.TryGetProperty("data", out var data))
        {
            _logger.LogInformation("Moyasar webhook {EventId} type={Type} (no data).", eventId, eventType);
            return Ok();
        }

        var payment = MoyasarApiClient.ParsePayment(data);
        if (payment is null)
        {
            _logger.LogWarning("Moyasar webhook {EventId} type={Type}: could not parse payment.", eventId, eventType);
            return Ok();
        }

        var handled = await _payments.HandleWebhookAsync(eventType, payment, ct);
        _logger.LogInformation(
            "Moyasar webhook {EventId} type={Type} payment={PaymentId} status={Status} token={HasToken} handled={Handled}.",
            eventId,
            eventType,
            payment.Id,
            payment.Status,
            !string.IsNullOrWhiteSpace(payment.SourceToken),
            handled);
        return Ok();
    }

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }
}
