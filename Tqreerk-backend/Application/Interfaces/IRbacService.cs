using Taqreerk.Application.DTOs.Rbac;

namespace Taqreerk.Application.Interfaces;

public interface IRbacService
{
    // Pages
    Task<IReadOnlyList<PageDto>> GetPagesAsync(CancellationToken ct = default);
    Task<PageDto> CreatePageAsync(CreatePageRequest req, CancellationToken ct = default);
    Task<PageDto> UpdatePageAsync(Guid pageId, UpdatePageRequest req, CancellationToken ct = default);
    Task DeletePageAsync(Guid pageId, CancellationToken ct = default);

    // Permissions (children of a page)
    Task<PermissionDto> CreatePermissionAsync(Guid pageId, CreatePermissionRequest req, CancellationToken ct = default);
    Task<PermissionDto> UpdatePermissionAsync(Guid permissionId, UpdatePermissionRequest req, CancellationToken ct = default);
    Task DeletePermissionAsync(Guid permissionId, CancellationToken ct = default);

    // Roles
    Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken ct = default);
    Task<RoleDetailDto> GetRoleAsync(Guid roleId, CancellationToken ct = default);
    Task<RoleDto> CreateRoleAsync(CreateRoleRequest req, CancellationToken ct = default);
    Task<RoleDto> UpdateRoleAsync(Guid roleId, UpdateRoleRequest req, CancellationToken ct = default);
    Task DeleteRoleAsync(Guid roleId, CancellationToken ct = default);

    Task<RoleDetailDto> SetRolePermissionsAsync(Guid roleId, SetRolePermissionsRequest req, CancellationToken ct = default);

    // User–Role
    Task<IReadOnlyList<UserRolesDto>> GetUsersWithRolesAsync(CancellationToken ct = default);
    Task<UserRolesDto> SetUserRolesAsync(Guid userId, SetUserRolesRequest req, CancellationToken ct = default);

    // Current user's resolved permissions
    Task<MePermissionsDto> GetEffectivePermissionsAsync(Guid userId, CancellationToken ct = default);

    // Used by TokenService when issuing a JWT
    Task<IReadOnlyList<string>> GetPermissionKeysForUserAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetRoleNamesForUserAsync(Guid userId, CancellationToken ct = default);
}
