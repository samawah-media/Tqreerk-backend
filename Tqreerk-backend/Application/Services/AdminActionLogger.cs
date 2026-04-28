using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Entities;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class AdminActionLogger : IAdminActionLogger
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
    };

    private readonly TaqreerkDbContext _db;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<AdminActionLogger> _logger;

    public AdminActionLogger(
        TaqreerkDbContext db,
        IHttpContextAccessor http,
        ILogger<AdminActionLogger> logger)
    {
        _db = db;
        _http = http;
        _logger = logger;
    }

    public async Task LogAsync(
        Guid? adminUserId,
        string actionType,
        string targetEntityType,
        Guid? targetEntityId,
        string? reason = null,
        object? beforeState = null,
        object? afterState = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actionType))
        {
            _logger.LogWarning("Skipping admin-action log with empty actionType");
            return;
        }

        try
        {
            var ctx = _http.HttpContext;

            var entry = new AdminActionLog
            {
                AdminUserId = adminUserId,
                ActionType = actionType.Trim(),
                TargetEntityType = string.IsNullOrWhiteSpace(targetEntityType)
                    ? "unknown"
                    : targetEntityType.Trim(),
                TargetEntityId = targetEntityId,
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                BeforeState = beforeState is null ? null : JsonSerializer.Serialize(beforeState, JsonOpts),
                AfterState = afterState is null ? null : JsonSerializer.Serialize(afterState, JsonOpts),
                IpAddress = ctx?.Connection?.RemoteIpAddress?.ToString(),
                UserAgent = TruncateOrNull(ctx?.Request?.Headers.UserAgent.ToString(), 500),
            };

            _db.AdminActionLogs.Add(entry);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Audit logging is intentionally fail-soft. Losing a row is
            // less bad than failing the action that produced it (the
            // user-visible behaviour stays consistent). We log loudly so
            // a missing audit trail is investigable.
            _logger.LogError(ex,
                "AdminActionLogger failed to persist {ActionType} on {Entity} {EntityId}",
                actionType, targetEntityType, targetEntityId);
        }
    }

    private static string? TruncateOrNull(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Length <= max ? value : value[..max];
    }
}
