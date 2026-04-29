using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class StaffService : IStaffService
{
    /// Names of roles a staff member can hold. Has to stay in sync with the
    /// platform-scoped roles seeded in RbacSeedData. Validated case-sensitively
    /// — the SPA picks from a fixed list so typos can't reach here.
    private static readonly HashSet<string> AllowedRoleNames =
        new(StringComparer.Ordinal) { "SuperAdmin", "Admin", "ContentReviewer" };

    private const string SuperAdminRoleName = "SuperAdmin";

    private readonly TaqreerkDbContext _db;
    private readonly ITwoFactorService _twoFactor;
    private readonly IAdminActionLogger _audit;

    public StaffService(
        TaqreerkDbContext db,
        ITwoFactorService twoFactor,
        IAdminActionLogger audit)
    {
        _db = db;
        _twoFactor = twoFactor;
        _audit = audit;
    }

    public async Task<IReadOnlyList<StaffListItemDto>> ListAsync(CancellationToken ct = default)
    {
        // Two queries on purpose: the staff list is short (<100 rows in
        // practice) and joining 2FA status into the LINQ projection forces
        // EF to materialise both tables anyway. Cleaner to read it as two
        // small queries and zip in memory.
        var rows = await _db.Users
            .AsNoTracking()
            .Where(u => u.IsPlatformStaff)
            .OrderByDescending(u => u.CreatedAt)
            .Select(u => new
            {
                u.Id,
                u.FullName,
                u.Email,
                u.CreatedAt,
                Roles = u.UserRoles
                    .Where(ur => ur.Role.Scope == RoleScope.Platform)
                    .Select(ur => ur.Role.Name)
                    .ToList(),
            })
            .ToListAsync(ct);

        var ids = rows.Select(r => r.Id).ToList();
        var twoFa = await _db.Admin2faSecrets
            .AsNoTracking()
            .Where(s => ids.Contains(s.UserId))
            .ToDictionaryAsync(s => s.UserId, ct);

        return rows.Select(r =>
        {
            twoFa.TryGetValue(r.Id, out var secret);
            return new StaffListItemDto(
                r.Id,
                r.FullName,
                r.Email,
                r.Roles,
                TwoFactorConfigured: secret is not null,
                TwoFactorEnabled: secret?.IsEnabled ?? false,
                TwoFactorLastUsedAt: secret?.LastUsedAt,
                CreatedAt: r.CreatedAt);
        }).ToList();
    }

    public async Task<StaffListItemDto> CreateAsync(
        Guid actingUserId, CreateStaffRequest request, CancellationToken ct = default)
    {
        if (!AllowedRoleNames.Contains(request.RoleName))
            throw new InvalidOperationException("Invalid platform role.");

        var emailLower = request.Email.Trim().ToLowerInvariant();

        var emailTaken = await _db.Users.AnyAsync(u => u.Email == emailLower, ct);
        if (emailTaken)
            throw new InvalidOperationException("A user with this email already exists.");

        var role = await _db.Roles
            .Where(r => r.Name == request.RoleName && r.Scope == RoleScope.Platform)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Platform role not found.");

        var user = new User
        {
            Email = emailLower,
            FullName = request.FullName.Trim(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            UserType = "individual",
            EmailVerified = true,
            Status = UserStatus.Active,
            IsPlatformStaff = true,
            PreferredLanguage = "ar",
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        _db.UserRoles.Add(new UserRole
        {
            UserId = user.Id,
            RoleId = role.Id,
        });
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "staff.create",
            targetEntityType: "User",
            targetEntityId: user.Id,
            afterState: new { user.Email, user.FullName, Role = role.Name },
            ct: ct);

        return new StaffListItemDto(
            user.Id,
            user.FullName,
            user.Email,
            new[] { role.Name },
            TwoFactorConfigured: false,
            TwoFactorEnabled: false,
            TwoFactorLastUsedAt: null,
            CreatedAt: user.CreatedAt);
    }

    public async Task<StaffListItemDto> UpdateRoleAsync(
        Guid actingUserId, Guid targetUserId, UpdateStaffRoleRequest request, CancellationToken ct = default)
    {
        if (!AllowedRoleNames.Contains(request.RoleName))
            throw new InvalidOperationException("Invalid platform role.");

        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Id == targetUserId, ct)
            ?? throw new KeyNotFoundException("User not found.");

        if (!user.IsPlatformStaff)
            throw new InvalidOperationException("Target user is not platform staff.");

        var newRole = await _db.Roles
            .Where(r => r.Name == request.RoleName && r.Scope == RoleScope.Platform)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Platform role not found.");

        var existing = await _db.UserRoles
            .Include(ur => ur.Role)
            .Where(ur => ur.UserId == targetUserId && ur.Role.Scope == RoleScope.Platform)
            .ToListAsync(ct);

        // Demoting the last SuperAdmin would lock the platform out — refuse.
        var isLosingSuperAdmin = existing.Any(ur => ur.Role.Name == SuperAdminRoleName)
                                 && request.RoleName != SuperAdminRoleName;
        if (isLosingSuperAdmin)
        {
            var otherSuperAdmins = await _db.UserRoles
                .Where(ur => ur.UserId != targetUserId
                          && ur.Role.Scope == RoleScope.Platform
                          && ur.Role.Name == SuperAdminRoleName)
                .CountAsync(ct);
            if (otherSuperAdmins == 0)
                throw new InvalidOperationException("Cannot demote the last SuperAdmin.");
        }

        var beforeRoles = existing.Select(ur => ur.Role.Name).ToList();

        _db.UserRoles.RemoveRange(existing);
        _db.UserRoles.Add(new UserRole { UserId = targetUserId, RoleId = newRole.Id });

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "staff.role.update",
            targetEntityType: "User",
            targetEntityId: targetUserId,
            beforeState: new { Roles = beforeRoles },
            afterState: new { Roles = new[] { newRole.Name } },
            ct: ct);

        return await BuildListItemAsync(targetUserId, ct);
    }

    public async Task DeleteAsync(Guid actingUserId, Guid targetUserId, CancellationToken ct = default)
    {
        if (actingUserId == targetUserId)
            throw new InvalidOperationException("You cannot delete your own account.");

        var user = await _db.Users
            .FirstOrDefaultAsync(u => u.Id == targetUserId, ct)
            ?? throw new KeyNotFoundException("User not found.");

        if (!user.IsPlatformStaff)
            throw new InvalidOperationException("Target user is not platform staff.");

        // Same last-SuperAdmin guard as UpdateRoleAsync.
        var isSuperAdmin = await _db.UserRoles
            .AnyAsync(ur => ur.UserId == targetUserId
                         && ur.Role.Scope == RoleScope.Platform
                         && ur.Role.Name == SuperAdminRoleName, ct);
        if (isSuperAdmin)
        {
            var otherSuperAdmins = await _db.UserRoles
                .Where(ur => ur.UserId != targetUserId
                          && ur.Role.Scope == RoleScope.Platform
                          && ur.Role.Name == SuperAdminRoleName)
                .CountAsync(ct);
            if (otherSuperAdmins == 0)
                throw new InvalidOperationException("Cannot delete the last SuperAdmin.");
        }

        // Soft-delete the user and clear the staff flag so a future undelete
        // can't silently restore admin access. Refresh tokens and 2FA secrets
        // cascade off the user — we leave the cascade for the soft-delete
        // path to deal with on a real rehydrate.
        user.DeletedAt = DateTime.UtcNow;
        user.IsPlatformStaff = false;

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "staff.delete",
            targetEntityType: "User",
            targetEntityId: targetUserId,
            beforeState: new { user.Email, user.FullName },
            ct: ct);
    }

    public async Task ResetTwoFactorAsync(Guid actingUserId, Guid targetUserId, CancellationToken ct = default)
    {
        var isStaff = await _db.Users
            .AnyAsync(u => u.Id == targetUserId && u.IsPlatformStaff, ct);
        if (!isStaff)
            throw new KeyNotFoundException("Staff user not found.");

        await _twoFactor.ResetAsync(targetUserId, ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "staff.2fa.reset",
            targetEntityType: "User",
            targetEntityId: targetUserId,
            ct: ct);
    }

    private async Task<StaffListItemDto> BuildListItemAsync(Guid userId, CancellationToken ct)
    {
        var u = await _db.Users
            .AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => new
            {
                x.Id,
                x.FullName,
                x.Email,
                x.CreatedAt,
                Roles = x.UserRoles
                    .Where(ur => ur.Role.Scope == RoleScope.Platform)
                    .Select(ur => ur.Role.Name)
                    .ToList(),
            })
            .FirstAsync(ct);

        var secret = await _db.Admin2faSecrets
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId, ct);

        return new StaffListItemDto(
            u.Id,
            u.FullName,
            u.Email,
            u.Roles,
            TwoFactorConfigured: secret is not null,
            TwoFactorEnabled: secret?.IsEnabled ?? false,
            TwoFactorLastUsedAt: secret?.LastUsedAt,
            CreatedAt: u.CreatedAt);
    }
}
