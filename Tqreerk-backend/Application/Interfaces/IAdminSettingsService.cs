using Taqreerk.Application.DTOs.Admin;

namespace Taqreerk.Application.Interfaces;

/// SuperAdmin surface for system_settings + maintenance toggle + health.
/// Settings are cached in IMemoryCache (30-second TTL) so the maintenance
/// middleware can read them on every request without a round-trip.
public interface IAdminSettingsService
{
    Task<IReadOnlyList<SystemSettingDto>> ListAsync(CancellationToken ct = default);

    /// Update one setting by key. Refuses to create a new key — keys are
    /// shipped in the seed and adding new ones requires a code change.
    Task<SystemSettingDto> UpdateAsync(
        Guid actingUserId, string key, UpdateSettingRequest req, CancellationToken ct = default);

    /// Convenience for "is the platform in maintenance mode right now".
    /// Cached so the middleware can call this on every request cheaply.
    Task<bool> IsMaintenanceModeAsync(CancellationToken ct = default);

    /// Atomically flip the maintenance flag and bust the cache so the
    /// middleware sees the new value on the next tick.
    Task SetMaintenanceModeAsync(
        Guid actingUserId, bool enabled, CancellationToken ct = default);

    Task<AdminHealthDto> GetHealthAsync(CancellationToken ct = default);
}
