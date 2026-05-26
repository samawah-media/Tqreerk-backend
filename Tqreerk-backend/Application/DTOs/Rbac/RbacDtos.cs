using Taqreerk.Domain.Enums;

namespace Taqreerk.Application.DTOs.Rbac;

// ── Catalog (what exists) ────────────────────────────────────────────────────

public record PermissionDto(
    Guid Id,
    Guid PageId,
    string Key,
    string NameEn,
    string NameAr,
    string? Description,
    bool IsSystem
);

public record PageDto(
    Guid Id,
    string Key,
    string NameEn,
    string NameAr,
    string? Description,
    int SortOrder,
    bool IsSystem,
    IReadOnlyList<PermissionDto> Permissions
);

public record RoleDto(
    Guid Id,
    string Name,
    string? Description,
    RoleScope Scope,
    bool IsSystem
);

// ── Role detail: "role has pages, pages have permissions (granted flag)" ─────

public record RolePermissionDto(
    Guid Id,
    string Key,
    string NameEn,
    string NameAr,
    bool Granted
);

public record RolePageDto(
    Guid Id,
    string Key,
    string NameEn,
    string NameAr,
    int SortOrder,
    IReadOnlyList<RolePermissionDto> Permissions
);

public record RoleDetailDto(
    Guid Id,
    string Name,
    string? Description,
    RoleScope Scope,
    bool IsSystem,
    IReadOnlyList<RolePageDto> Pages
);

// ── Users & their roles ──────────────────────────────────────────────────────

public record UserRolesDto(
    Guid UserId,
    string Email,
    string FullName,
    IReadOnlyList<RoleDto> Roles
);

// ── Requests ────────────────────────────────────────────────────────────────

public record CreatePageRequest(string Key, string NameEn, string NameAr, string? Description, int SortOrder);
public record UpdatePageRequest(string NameEn, string NameAr, string? Description, int SortOrder);

public record CreatePermissionRequest(string Key, string NameEn, string NameAr, string? Description);
public record UpdatePermissionRequest(string NameEn, string NameAr, string? Description);

public record CreateRoleRequest(string Name, string? Description, RoleScope Scope);
public record UpdateRoleRequest(string Name, string? Description, RoleScope Scope);

public record SetRolePermissionsRequest(IReadOnlyList<Guid> PermissionIds);
public record SetUserRolesRequest(IReadOnlyList<Guid> RoleIds);

// ── /me/permissions response ────────────────────────────────────────────────

public record MePermissionsDto(
    Guid UserId,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> PermissionKeys,
    IReadOnlyList<RolePageDto> Pages
);
