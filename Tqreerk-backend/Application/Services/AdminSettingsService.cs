using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Settings;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class AdminSettingsService : IAdminSettingsService
{
    public const string MaintenanceKey = "maintenance.enabled";
    private const string MaintenanceCacheKey = "system_settings:maintenance.enabled";
    private static readonly TimeSpan MaintenanceCacheTtl = TimeSpan.FromSeconds(30);

    private readonly TaqreerkDbContext _db;
    private readonly IAdminActionLogger _audit;
    private readonly IMemoryCache _cache;
    private readonly IHttpClientFactory _httpFactory;
    private readonly AiServiceSettings _aiSettings;
    private readonly IHostEnvironment _env;
    private readonly ILogger<AdminSettingsService> _logger;

    public AdminSettingsService(
        TaqreerkDbContext db,
        IAdminActionLogger audit,
        IMemoryCache cache,
        IHttpClientFactory httpFactory,
        IOptions<AiServiceSettings> aiSettings,
        IHostEnvironment env,
        ILogger<AdminSettingsService> logger)
    {
        _db = db;
        _audit = audit;
        _cache = cache;
        _httpFactory = httpFactory;
        _aiSettings = aiSettings.Value;
        _env = env;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SystemSettingDto>> ListAsync(CancellationToken ct = default)
    {
        return await _db.SystemSettings
            .AsNoTracking()
            .OrderBy(s => s.Category).ThenBy(s => s.Key)
            .Select(s => new SystemSettingDto(s.Key, s.Value, s.Category, s.ValueType, s.Description))
            .ToListAsync(ct);
    }

    public async Task<SystemSettingDto> UpdateAsync(
        Guid actingUserId, string key, UpdateSettingRequest req, CancellationToken ct = default)
    {
        var setting = await _db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key, ct)
            ?? throw new KeyNotFoundException("Setting not found.");

        // Best-effort type validation. We don't enforce it strictly because
        // some types (decimal in different locales) are easier to round-trip
        // as strings — but obvious bool/int typos should still fail fast.
        if (setting.ValueType == "bool"
            && !bool.TryParse(req.Value, out _))
            throw new InvalidOperationException("Value must be 'true' or 'false'.");
        if (setting.ValueType == "int"
            && !int.TryParse(req.Value, out _))
            throw new InvalidOperationException("Value must be an integer.");

        var before = setting.Value;
        setting.Value = req.Value;
        setting.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // Bust any cached entries derived from this setting so middleware
        // and other readers see the new value within one TTL window.
        if (key == MaintenanceKey) _cache.Remove(MaintenanceCacheKey);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "settings.update",
            targetEntityType: "SystemSetting",
            targetEntityId: setting.Id,
            beforeState: new { setting.Key, Value = before },
            afterState: new { setting.Key, setting.Value },
            ct: ct);

        return new SystemSettingDto(setting.Key, setting.Value, setting.Category, setting.ValueType, setting.Description);
    }

    public Task<bool> IsMaintenanceModeAsync(CancellationToken ct = default)
        => _cache.GetOrCreateAsync(MaintenanceCacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = MaintenanceCacheTtl;
            var raw = await _db.SystemSettings
                .AsNoTracking()
                .Where(s => s.Key == MaintenanceKey)
                .Select(s => s.Value)
                .FirstOrDefaultAsync(ct);
            return bool.TryParse(raw, out var v) && v;
        });

    public async Task SetMaintenanceModeAsync(
        Guid actingUserId, bool enabled, CancellationToken ct = default)
    {
        await UpdateAsync(actingUserId, MaintenanceKey,
            new UpdateSettingRequest(enabled ? "true" : "false"), ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: enabled ? "maintenance.enable" : "maintenance.disable",
            targetEntityType: "SystemSetting",
            targetEntityId: null,
            ct: ct);
    }

    public async Task<AdminHealthDto> GetHealthAsync(CancellationToken ct = default)
    {
        var checks = new List<HealthCheckItemDto>();
        var now = DateTime.UtcNow;

        // ── Database ──────────────────────────────────────────────────
        try
        {
            var canConnect = await _db.Database.CanConnectAsync(ct);
            checks.Add(new HealthCheckItemDto(
                "database",
                canConnect ? "healthy" : "unreachable",
                canConnect ? null : "CanConnectAsync returned false",
                now));
        }
        catch (Exception ex)
        {
            checks.Add(new HealthCheckItemDto("database", "unreachable", ex.Message, now));
        }

        // ── AI service ────────────────────────────────────────────────
        if (string.IsNullOrWhiteSpace(_aiSettings.BaseUrl))
        {
            checks.Add(new HealthCheckItemDto(
                "ai_service", "unknown", "AiService BaseUrl not configured", now));
        }
        else
        {
            try
            {
                using var client = _httpFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                using var resp = await client.GetAsync(
                    new Uri(new Uri(_aiSettings.BaseUrl), "/health"), ct);
                checks.Add(new HealthCheckItemDto(
                    "ai_service",
                    resp.IsSuccessStatusCode ? "healthy" : "degraded",
                    $"HTTP {(int)resp.StatusCode}",
                    now));
            }
            catch (Exception ex)
            {
                checks.Add(new HealthCheckItemDto("ai_service", "unreachable", ex.Message, now));
            }
        }

        // ── Stubs for services we haven't wired health probes for yet —
        // surfacing them in the response so the SPA can render a known
        // shape instead of guessing what's covered.
        checks.Add(new HealthCheckItemDto("redis", "unknown", "Health probe not wired", now));
        checks.Add(new HealthCheckItemDto("gcs",   "unknown", "Health probe not wired", now));
        checks.Add(new HealthCheckItemDto("smtp",  "unknown", "Health probe not wired", now));

        var aggregate = checks.Any(c => c.Status == "unreachable")
            ? "unhealthy"
            : checks.Any(c => c.Status == "degraded") ? "degraded"
            : checks.All(c => c.Status == "healthy") ? "healthy"
            : "unknown";

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0";

        return new AdminHealthDto(
            aggregate,
            _env.EnvironmentName,
            version,
            now,
            checks);
    }
}
