namespace Taqreerk.Application.DTOs.Admin;

/// Profile shape returned by GET /api/admin/auth/me. Carries everything the
/// admin SPA needs to render the layout: identity, role names, and the
/// permission bag (page key -> action keys) consumed by the v-permission
/// directive.
public record AdminProfileDto(
    Guid Id,
    string FullName,
    string Email,
    string PreferredLanguage,
    bool IsPlatformStaff,
    /// Names of the platform-scoped roles the user holds, e.g.
    /// ["SuperAdmin"] or ["Admin", "ContentReviewer"]. Lower-case copies
    /// are not provided — the SPA matches role names verbatim.
    IReadOnlyList<string> Roles,
    /// Granular permission bag: { "reports": ["view","edit","delete"], ... }
    /// Built by aggregating role_permissions across all the user's roles.
    IReadOnlyDictionary<string, IReadOnlyList<string>> Permissions
);
