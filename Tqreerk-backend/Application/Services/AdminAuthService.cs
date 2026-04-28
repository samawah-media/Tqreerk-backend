using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class AdminAuthService : IAdminAuthService
{
    private readonly TaqreerkDbContext _db;

    public AdminAuthService(TaqreerkDbContext db)
    {
        _db = db;
    }

    public async Task<AdminProfileDto> GetMyProfileAsync(Guid userId, CancellationToken ct = default)
    {
        // Single round-trip: load user + their platform-scoped roles + the
        // permissions those roles grant (with the page key for grouping).
        // We don't need org-scoped roles here — the admin app cares only
        // about platform staff RBAC.
        var user = await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                u.Id,
                u.FullName,
                u.Email,
                u.PreferredLanguage,
                u.IsPlatformStaff,
            })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("User not found.");

        if (!user.IsPlatformStaff)
        {
            // The controller catches this and returns 403, distinct from a
            // missing/expired JWT (which Authorization middleware turns into
            // 401 before we ever get here).
            throw new ForbiddenException("This account is not a platform staff member.");
        }

        // Roles the user holds in platform scope.
        var roleNames = await _db.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == userId && ur.Role.Scope == RoleScope.Platform)
            .Select(ur => ur.Role.Name)
            .ToListAsync(ct);

        // Build the page -> [permission keys] dictionary by aggregating
        // role_permissions across every role the user holds (platform scope
        // only). De-duplicate at the (pageKey, permissionKey) tuple.
        var perms = await _db.UserRoles
            .AsNoTracking()
            .Where(ur => ur.UserId == userId && ur.Role.Scope == RoleScope.Platform)
            .SelectMany(ur => ur.Role.RolePermissions.Select(rp => new
            {
                PageKey = rp.Permission.Page.Key,
                PermissionKey = rp.Permission.Key,
            }))
            .Distinct()
            .ToListAsync(ct);

        var permissionBag = perms
            .GroupBy(p => p.PageKey)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(p => p.PermissionKey).Distinct().ToList());

        return new AdminProfileDto(
            user.Id,
            user.FullName,
            user.Email,
            user.PreferredLanguage,
            user.IsPlatformStaff,
            roleNames,
            permissionBag
        );
    }
}

/// 403-mapping exception. Distinct from UnauthorizedAccessException (which
/// the global handler turns into 401) — the admin app treats 401 as "log in
/// again" and 403 as "you're not staff" so the two need different signals.
public class ForbiddenException : Exception
{
    public ForbiddenException(string message) : base(message) { }
}
