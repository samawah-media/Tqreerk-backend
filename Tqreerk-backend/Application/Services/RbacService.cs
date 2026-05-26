using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Rbac;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Entities;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class RbacService : IRbacService
{
    private readonly TaqreerkDbContext _db;

    public RbacService(TaqreerkDbContext db) => _db = db;

    // ── Pages ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<PageDto>> GetPagesAsync(CancellationToken ct = default)
    {
        var pages = await _db.Pages
            .AsNoTracking()
            .Include(p => p.Permissions)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.NameEn)
            .ToListAsync(ct);

        return pages.Select(ToPageDto).ToList();
    }

    public async Task<PageDto> CreatePageAsync(CreatePageRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Key))
            throw new ArgumentException("Page key is required.");

        if (await _db.Pages.AnyAsync(p => p.Key == req.Key, ct))
            throw new InvalidOperationException($"Page with key '{req.Key}' already exists.");

        var page = new Page
        {
            Key = req.Key.Trim().ToLowerInvariant(),
            NameEn = req.NameEn,
            NameAr = req.NameAr,
            Description = req.Description,
            SortOrder = req.SortOrder,
            IsSystem = false,
        };

        _db.Pages.Add(page);
        await _db.SaveChangesAsync(ct);

        return ToPageDto(page);
    }

    public async Task<PageDto> UpdatePageAsync(Guid pageId, UpdatePageRequest req, CancellationToken ct = default)
    {
        var page = await _db.Pages.Include(p => p.Permissions).FirstOrDefaultAsync(p => p.Id == pageId, ct)
            ?? throw new KeyNotFoundException("Page not found.");

        page.NameEn = req.NameEn;
        page.NameAr = req.NameAr;
        page.Description = req.Description;
        page.SortOrder = req.SortOrder;

        await _db.SaveChangesAsync(ct);
        return ToPageDto(page);
    }

    public async Task DeletePageAsync(Guid pageId, CancellationToken ct = default)
    {
        var page = await _db.Pages.FirstOrDefaultAsync(p => p.Id == pageId, ct)
            ?? throw new KeyNotFoundException("Page not found.");

        if (page.IsSystem)
            throw new InvalidOperationException("System pages cannot be deleted.");

        _db.Pages.Remove(page);
        await _db.SaveChangesAsync(ct);
    }

    // ── Permissions ──────────────────────────────────────────────────────────

    public async Task<PermissionDto> CreatePermissionAsync(Guid pageId, CreatePermissionRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Key))
            throw new ArgumentException("Permission key is required.");

        var page = await _db.Pages.FirstOrDefaultAsync(p => p.Id == pageId, ct)
            ?? throw new KeyNotFoundException("Page not found.");

        var key = req.Key.Trim().ToLowerInvariant();

        if (await _db.Permissions.AnyAsync(p => p.PageId == pageId && p.Key == key, ct))
            throw new InvalidOperationException($"Permission '{key}' already exists on this page.");

        var permission = new Permission
        {
            PageId = page.Id,
            Key = key,
            NameEn = req.NameEn,
            NameAr = req.NameAr,
            Description = req.Description,
            IsSystem = false,
        };

        _db.Permissions.Add(permission);
        await _db.SaveChangesAsync(ct);

        return ToPermissionDto(permission);
    }

    public async Task<PermissionDto> UpdatePermissionAsync(Guid permissionId, UpdatePermissionRequest req, CancellationToken ct = default)
    {
        var permission = await _db.Permissions.FirstOrDefaultAsync(p => p.Id == permissionId, ct)
            ?? throw new KeyNotFoundException("Permission not found.");

        permission.NameEn = req.NameEn;
        permission.NameAr = req.NameAr;
        permission.Description = req.Description;

        await _db.SaveChangesAsync(ct);
        return ToPermissionDto(permission);
    }

    public async Task DeletePermissionAsync(Guid permissionId, CancellationToken ct = default)
    {
        var permission = await _db.Permissions.FirstOrDefaultAsync(p => p.Id == permissionId, ct)
            ?? throw new KeyNotFoundException("Permission not found.");

        if (permission.IsSystem)
            throw new InvalidOperationException("System permissions cannot be deleted.");

        _db.Permissions.Remove(permission);
        await _db.SaveChangesAsync(ct);
    }

    // ── Roles ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<RoleDto>> GetRolesAsync(CancellationToken ct = default)
    {
        var roles = await _db.Roles.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);
        return roles.Select(ToRoleDto).ToList();
    }

    public async Task<RoleDetailDto> GetRoleAsync(Guid roleId, CancellationToken ct = default)
    {
        var role = await _db.Roles
            .AsNoTracking()
            .Include(r => r.RolePermissions)
            .FirstOrDefaultAsync(r => r.Id == roleId, ct)
            ?? throw new KeyNotFoundException("Role not found.");

        var grantedPermissionIds = role.RolePermissions.Select(rp => rp.PermissionId).ToHashSet();

        var pages = await _db.Pages
            .AsNoTracking()
            .Include(p => p.Permissions)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.NameEn)
            .ToListAsync(ct);

        var pageDtos = pages.Select(p => new RolePageDto(
            p.Id,
            p.Key,
            p.NameEn,
            p.NameAr,
            p.SortOrder,
            p.Permissions
                .OrderBy(x => x.Key)
                .Select(x => new RolePermissionDto(
                    x.Id, x.Key, x.NameEn, x.NameAr,
                    grantedPermissionIds.Contains(x.Id)))
                .ToList()
        )).ToList();

        return new RoleDetailDto(role.Id, role.Name, role.Description, role.Scope, role.IsSystem, pageDtos);
    }

    public async Task<RoleDto> CreateRoleAsync(CreateRoleRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new ArgumentException("Role name is required.");

        if (await _db.Roles.AnyAsync(r => r.Name == req.Name, ct))
            throw new InvalidOperationException($"Role '{req.Name}' already exists.");

        var role = new Role
        {
            Name = req.Name,
            Description = req.Description,
            Scope = req.Scope,
            IsSystem = false,
        };

        _db.Roles.Add(role);
        await _db.SaveChangesAsync(ct);

        return ToRoleDto(role);
    }

    public async Task<RoleDto> UpdateRoleAsync(Guid roleId, UpdateRoleRequest req, CancellationToken ct = default)
    {
        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct)
            ?? throw new KeyNotFoundException("Role not found.");

        if (role.IsSystem && role.Name != req.Name)
            throw new InvalidOperationException("System roles cannot be renamed.");

        role.Name = req.Name;
        role.Description = req.Description;
        role.Scope = req.Scope;

        await _db.SaveChangesAsync(ct);
        return ToRoleDto(role);
    }

    public async Task DeleteRoleAsync(Guid roleId, CancellationToken ct = default)
    {
        var role = await _db.Roles.FirstOrDefaultAsync(r => r.Id == roleId, ct)
            ?? throw new KeyNotFoundException("Role not found.");

        if (role.IsSystem)
            throw new InvalidOperationException("System roles cannot be deleted.");

        _db.Roles.Remove(role);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<RoleDetailDto> SetRolePermissionsAsync(Guid roleId, SetRolePermissionsRequest req, CancellationToken ct = default)
    {
        var role = await _db.Roles
            .Include(r => r.RolePermissions)
            .FirstOrDefaultAsync(r => r.Id == roleId, ct)
            ?? throw new KeyNotFoundException("Role not found.");

        var requestedIds = req.PermissionIds.Distinct().ToHashSet();

        // Validate all requested permission IDs exist
        var existingIds = await _db.Permissions
            .Where(p => requestedIds.Contains(p.Id))
            .Select(p => p.Id)
            .ToListAsync(ct);

        var missing = requestedIds.Except(existingIds).ToList();
        if (missing.Count > 0)
            throw new ArgumentException($"Unknown permission IDs: {string.Join(", ", missing)}");

        var currentIds = role.RolePermissions.Select(rp => rp.PermissionId).ToHashSet();

        var toRemove = role.RolePermissions.Where(rp => !requestedIds.Contains(rp.PermissionId)).ToList();
        foreach (var rp in toRemove) _db.RolePermissions.Remove(rp);

        foreach (var id in requestedIds.Except(currentIds))
            _db.RolePermissions.Add(new RolePermission { RoleId = roleId, PermissionId = id });

        await _db.SaveChangesAsync(ct);

        return await GetRoleAsync(roleId, ct);
    }

    // ── User–Role ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<UserRolesDto>> GetUsersWithRolesAsync(CancellationToken ct = default)
    {
        var users = await _db.Users
            .AsNoTracking()
            .Include(u => u.UserRoles)
                .ThenInclude(ur => ur.Role)
            .OrderBy(u => u.Email)
            .ToListAsync(ct);

        return users.Select(u => new UserRolesDto(
            u.Id, u.Email, u.FullName,
            u.UserRoles.Select(ur => ToRoleDto(ur.Role)).ToList()
        )).ToList();
    }

    public async Task<UserRolesDto> SetUserRolesAsync(Guid userId, SetUserRolesRequest req, CancellationToken ct = default)
    {
        var user = await _db.Users
            .Include(u => u.UserRoles)
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("User not found.");

        var requestedIds = req.RoleIds.Distinct().ToHashSet();

        var existingIds = await _db.Roles
            .Where(r => requestedIds.Contains(r.Id))
            .Select(r => r.Id)
            .ToListAsync(ct);

        var missing = requestedIds.Except(existingIds).ToList();
        if (missing.Count > 0)
            throw new ArgumentException($"Unknown role IDs: {string.Join(", ", missing)}");

        var currentIds = user.UserRoles.Select(ur => ur.RoleId).ToHashSet();

        var toRemove = user.UserRoles.Where(ur => !requestedIds.Contains(ur.RoleId)).ToList();
        foreach (var ur in toRemove) _db.UserRoles.Remove(ur);

        foreach (var id in requestedIds.Except(currentIds))
            _db.UserRoles.Add(new UserRole { UserId = userId, RoleId = id });

        await _db.SaveChangesAsync(ct);

        var roles = await _db.Roles
            .Where(r => requestedIds.Contains(r.Id))
            .OrderBy(r => r.Name)
            .ToListAsync(ct);

        return new UserRolesDto(user.Id, user.Email, user.FullName, roles.Select(ToRoleDto).ToList());
    }

    // ── Effective permissions ───────────────────────────────────────────────

    public async Task<MePermissionsDto> GetEffectivePermissionsAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("User not found.");

        var roleNames = await _db.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .Select(ur => ur.Role.Name)
            .ToListAsync(ct);

        var grantedPermissionIds = await _db.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .SelectMany(ur => ur.Role.RolePermissions)
            .Select(rp => rp.PermissionId)
            .Distinct()
            .ToListAsync(ct);

        var grantedSet = grantedPermissionIds.ToHashSet();

        var pages = await _db.Pages
            .AsNoTracking()
            .Include(p => p.Permissions)
            .OrderBy(p => p.SortOrder)
            .ToListAsync(ct);

        var permissionKeys = new List<string>();
        var pageDtos = new List<RolePageDto>();

        foreach (var page in pages)
        {
            var perms = page.Permissions
                .OrderBy(x => x.Key)
                .Select(x =>
                {
                    var granted = grantedSet.Contains(x.Id);
                    if (granted) permissionKeys.Add($"{page.Key}:{x.Key}");
                    return new RolePermissionDto(x.Id, x.Key, x.NameEn, x.NameAr, granted);
                })
                .ToList();

            pageDtos.Add(new RolePageDto(page.Id, page.Key, page.NameEn, page.NameAr, page.SortOrder, perms));
        }

        return new MePermissionsDto(user.Id, roleNames, permissionKeys, pageDtos);
    }

    public async Task<IReadOnlyList<string>> GetPermissionKeysForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var rows = await _db.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .SelectMany(ur => ur.Role.RolePermissions)
            .Select(rp => new { rp.Permission.Page.Key, PermKey = rp.Permission.Key })
            .Distinct()
            .ToListAsync(ct);

        return rows.Select(r => $"{r.Key}:{r.PermKey}").ToList();
    }

    public async Task<IReadOnlyList<string>> GetRoleNamesForUserAsync(Guid userId, CancellationToken ct = default)
    {
        return await _db.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .Select(ur => ur.Role.Name)
            .ToListAsync(ct);
    }

    // ── Mappers ──────────────────────────────────────────────────────────────

    private static RoleDto ToRoleDto(Role r)
        => new(r.Id, r.Name, r.Description, r.Scope, r.IsSystem);

    private static PermissionDto ToPermissionDto(Permission p)
        => new(p.Id, p.PageId, p.Key, p.NameEn, p.NameAr, p.Description, p.IsSystem);

    private static PageDto ToPageDto(Page p)
        => new(
            p.Id, p.Key, p.NameEn, p.NameAr, p.Description, p.SortOrder, p.IsSystem,
            p.Permissions.OrderBy(x => x.Key).Select(ToPermissionDto).ToList()
        );
}
