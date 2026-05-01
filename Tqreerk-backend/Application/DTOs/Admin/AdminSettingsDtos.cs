using System.ComponentModel.DataAnnotations;

namespace Taqreerk.Application.DTOs.Admin;

/// One row in GET /api/admin/settings. Frontend groups by Category to
/// render tabs.
public record SystemSettingDto(
    string Key,
    string Value,
    string Category,
    string ValueType,
    string? Description
);

/// Body of PATCH /api/admin/settings/{key}.
public record UpdateSettingRequest(
    [Required, StringLength(4000)] string Value
);

/// Per-service health row.
public record HealthCheckItemDto(
    string Service,
    /// "healthy" | "degraded" | "unreachable" | "unknown"
    string Status,
    string? Detail,
    DateTime CheckedAt
);

/// Full response of GET /api/admin/health.
public record AdminHealthDto(
    /// Aggregate state — "healthy" only when every individual check is.
    string Status,
    string Environment,
    string Version,
    DateTime ServerTimeUtc,
    IReadOnlyList<HealthCheckItemDto> Services
);
