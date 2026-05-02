using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

/// One row per platform-wide setting. Values are stored as strings; the
/// frontend casts based on ValueType. Categories are free-form so adding
/// new groups doesn't need an enum migration — the seed defines the
/// canonical set.
///
/// Designed so a SuperAdmin can edit a value without us shipping a new
/// release. The maintenance flag uses the well-known key
/// "maintenance.enabled" with ValueType="bool".
public class SystemSetting : AuditableEntity
{
    /// Stable identifier, e.g. "site_name", "maintenance.enabled",
    /// "reviews.auto_release_minutes". Lowercase + dot-separated.
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    /// Category bucket the SPA renders the setting under: General /
    /// Limits / Reviews / AI / Featured / Email / Maintenance.
    public string Category { get; set; } = string.Empty;

    /// Hint for the SPA: "string" | "bool" | "int" | "decimal". Validation
    /// is best-effort — server stores whatever the client sent and the
    /// reader (whoever consumes the setting) is expected to parse.
    public string ValueType { get; set; } = "string";

    /// Optional description shown next to the field.
    public string? Description { get; set; }

    /// True for settings shipped with the seed. The admin can edit them
    /// but cannot delete them — IsSystem keeps the row in place even
    /// across migrations that re-seed.
    public bool IsSystem { get; set; }
}
