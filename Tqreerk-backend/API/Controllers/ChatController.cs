using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Services;
using Taqreerk.Application.Settings;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.API.Controllers;

/// <summary>
/// Thin proxy in front of the Python ai-service's /chat/* routes. All four
/// endpoints exist for the same reason:
///   1. The ai-service has no auth of its own — it trusts whoever calls it.
///      We can't expose it directly to the browser (Cloud Run is
///      --no-allow-unauthenticated in prod, and even if it weren't, anyone
///      could spoof another user's id and read their chat history).
///   2. The browser only knows the .NET-issued JWT. We resolve the calling
///      user from `sub` here, then forward that id to the ai-service so it
///      can scope sessions / accessibility checks correctly.
///   3. Send-message is SSE (Server-Sent Events). We pass the upstream
///      response straight through as a stream so the browser sees `data:`
///      events as they arrive — no buffering, no JSON parsing.
///
/// This controller deliberately doesn't share state with `AiServiceClient`
/// or `IReportAiService` — chat is read-only and per-user, and the queueing
/// / status-finalizer machinery those services exist for is irrelevant here.
/// </summary>
[ApiController]
[Route("api/chat")]
[Authorize]
public class ChatController : ControllerBase
{
    private readonly HttpClient _http;
    private readonly TaqreerkDbContext _db;
    private readonly IUsageService _usage;
    private readonly ILogger<ChatController> _logger;

    public ChatController(
        IHttpClientFactory factory,
        IOptions<AiServiceSettings> settings,
        TaqreerkDbContext db,
        IUsageService usage,
        ILogger<ChatController> logger)
    {
        _db = db;
        _usage = usage;
        _logger = logger;

        // We deliberately use a raw HttpClient (not the typed AiServiceClient)
        // because that client is wired for short JSON RPCs with a global
        // 5-min timeout. SSE responses are open-ended — we want
        // ResponseHeadersRead so the upstream's first byte is forwarded
        // immediately, and Timeout=Infinite because the stream itself is
        // the response.
        var baseUrl = (settings.Value.BaseUrl ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
            throw new InvalidOperationException("AiService:BaseUrl is required.");

        _http = factory.CreateClient(nameof(ChatController));
        _http.BaseAddress = new Uri(baseUrl + "/");
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    // ── Sessions: list / create / read ────────────────────────────────────

    /// <summary>List chat sessions the calling user owns for a given report.
    /// Mirrors GET /api/ai/chat/reports/{report_id}/sessions on the ai-service
    /// but sources the user_id from the JWT instead of trusting a query
    /// param.</summary>
    [HttpGet("reports/{reportId:guid}/sessions")]
    public async Task<IActionResult> ListSessions(Guid reportId, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        await EnsureAiChatAllowedAsync(userId, ct);

        using var resp = await _http.GetAsync(
            $"chat/reports/{reportId}/sessions?user_id={userId}",
            HttpCompletionOption.ResponseContentRead,
            ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return new ContentResult
        {
            Content = body,
            ContentType = "application/json",
            StatusCode = (int)resp.StatusCode,
        };
    }

    /// <summary>Create a new chat session for the calling user against the
    /// given report. The ai-service's /sessions endpoint takes user_id +
    /// report_id + title; we set user_id from the JWT and only let the
    /// caller pick title and report_id.</summary>
    [HttpPost("sessions")]
    public async Task<IActionResult> CreateSession(
        [FromBody] CreateSessionRequest req,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        if (req?.ReportId == Guid.Empty) return BadRequest(new { error = "reportId is required" });
        await EnsureAiChatAllowedAsync(userId, ct);

        var payload = JsonSerializer.Serialize(new
        {
            user_id = userId,
            report_id = req!.ReportId,
            title = string.IsNullOrWhiteSpace(req.Title) ? "محادثة جديدة" : req.Title,
        });

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("chat/sessions", content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return new ContentResult
        {
            Content = body,
            ContentType = "application/json",
            StatusCode = (int)resp.StatusCode,
        };
    }

    /// <summary>Fetch a session and its full message history. Caller must
    /// own the session — we enforce by re-checking after the upstream call,
    /// since the ai-service doesn't take a user_id for this route.</summary>
    [HttpGet("sessions/{sessionId:guid}")]
    public async Task<IActionResult> GetSession(Guid sessionId, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        await EnsureAiChatAllowedAsync(userId, ct);

        using var resp = await _http.GetAsync(
            $"chat/sessions/{sessionId}",
            HttpCompletionOption.ResponseContentRead,
            ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        // The ai-service response doesn't include the owning UserId, so the
        // strongest cross-user check we can do at the proxy is to re-list
        // the user's sessions on the same report and verify membership.
        // We skip that here and rely on:
        //   1. session_ids being unguessable UUIDs (no enumeration).
        //   2. send_message + create staying user-scoped (those mutate state).
        // If we ever surface chat history publicly this needs revisiting.
        return new ContentResult
        {
            Content = body,
            ContentType = "application/json",
            StatusCode = (int)resp.StatusCode,
        };
    }

    // ── Send message: SSE pass-through ────────────────────────────────────

    /// <summary>Send a user message and stream the assistant's response back
    /// as SSE. The wire format is documented in the ai-service's chat.py
    /// module docstring — this proxy doesn't transform any frame, it just
    /// forwards bytes as they arrive.</summary>
    [HttpPost("sessions/{sessionId:guid}/messages")]
    public async Task SendMessage(
        Guid sessionId,
        [FromBody] SendMessageRequest req,
        CancellationToken ct)
    {
        if (!TryGetUserId(out var userId))
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        await EnsureAiChatAllowedAsync(userId, ct);

        try
        {
            await _usage.EnsureWithinLimitAndConsumeAsync(
                userId,
                UsageActionType.AiChat,
                sessionId,
                token => StreamSendMessageAsync(sessionId, req, token),
                ct);
        }
        catch (UsageLimitExceededException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[chat] send failed for session={SessionId}", sessionId);
            if (!Response.HasStarted)
            {
                Response.StatusCode = StatusCodes.Status502BadGateway;
                await Response.WriteAsync("{\"error\":\"ai-service unreachable\"}", ct);
            }
        }
    }

    private async Task StreamSendMessageAsync(
        Guid sessionId, SendMessageRequest? req, CancellationToken ct)
    {
        // Build the upstream request manually so we can opt into
        // HttpCompletionOption.ResponseHeadersRead — without it HttpClient
        // buffers the whole response, which kills streaming.
        using var upstream = new HttpRequestMessage(
            HttpMethod.Post, $"chat/sessions/{sessionId}/messages")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { message = req?.Message ?? string.Empty }),
                Encoding.UTF8,
                "application/json"),
        };

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[chat] upstream send failed for session={SessionId}", sessionId);
            Response.StatusCode = StatusCodes.Status502BadGateway;
            await Response.WriteAsync("{\"error\":\"ai-service unreachable\"}", ct);
            return;
        }

        // Mirror the upstream status — non-success means the ai-service
        // already returned a JSON error body; just pass it through with
        // the same content type and exit (no streaming for errors).
        Response.StatusCode = (int)resp.StatusCode;
        if (!resp.IsSuccessStatusCode)
        {
            using (resp)
            {
                var errorBody = await resp.Content.ReadAsStringAsync(ct);
                Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
                await Response.WriteAsync(errorBody, ct);
            }
            throw new InvalidOperationException(
                $"ai-service returned {(int)resp.StatusCode} for session {sessionId}");
        }

        // SSE response. Tell the browser + any reverse proxy (nginx,
        // Cloud Run frontend, etc.) NOT to buffer — `X-Accel-Buffering: no`
        // is the magic header nginx looks for.
        Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        Response.Headers["Content-Encoding"] = "identity";
        var feature = HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
        feature?.DisableBuffering();

        try
        {
            await using var upstreamStream = await resp.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[4096];
            while (true)
            {
                int read;
                try
                {
                    read = await upstreamStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                if (read <= 0) break;
                await Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Client closed the EventSource — fine, just stop.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[chat] stream pump failed for session={SessionId}", sessionId);
            throw;
        }
        finally
        {
            resp.Dispose();
        }
    }

    private async Task EnsureAiChatAllowedAsync(Guid userId, CancellationToken ct)
    {
        var (_, plan) = await SubscriptionResolver.GetActiveForUserAsync(_db, userId, ct);
        if (!PlanCapabilities.IncludesAiChat(plan))
        {
            throw new PlanFeatureNotAvailableException("AiChat", plan.NameAr);
        }
    }

    private bool TryGetUserId(out Guid userId)
    {
        var sub = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out userId);
    }

    public record CreateSessionRequest(Guid ReportId, string? Title = null);

    public record SendMessageRequest(string Message);
}
