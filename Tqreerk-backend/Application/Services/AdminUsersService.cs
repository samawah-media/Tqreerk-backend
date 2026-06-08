using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Admin;
using Taqreerk.Application.DTOs.Reports;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class AdminUsersService : IAdminUsersService
{
    private const int MaxPageSize = 100;

    private readonly TaqreerkDbContext _db;
    private readonly IAdminActionLogger _audit;

    public AdminUsersService(TaqreerkDbContext db, IAdminActionLogger audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<PagedResult<AdminUserListItemDto>> ListAsync(
        AdminUsersListRequest req, CancellationToken ct = default)
    {
        var page = Math.Max(1, req.Page);
        var pageSize = Math.Clamp(req.PageSize, 1, MaxPageSize);

        var q = _db.Users.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(req.Q))
        {
            var qLower = req.Q.Trim().ToLower();
            q = q.Where(u =>
                u.FullName.ToLower().Contains(qLower) ||
                u.Email.ToLower().Contains(qLower));
        }

        if (!string.IsNullOrWhiteSpace(req.Status)
            && Enum.TryParse<UserStatus>(req.Status, ignoreCase: true, out var status))
            q = q.Where(u => u.Status == status);

        if (req.CountryId is { } cid)
            q = q.Where(u => u.CountryId == cid);

        // userType is derived, not stored. Translate the requested category
        // into the matching filter expression.
        if (!string.IsNullOrWhiteSpace(req.UserType))
        {
            switch (req.UserType.ToLowerInvariant())
            {
                case "staff":
                    q = q.Where(u => u.IsPlatformStaff);
                    break;
                case "orgmember":
                    q = q.Where(u => !u.IsPlatformStaff
                                  && u.OrganizationMemberships.Any());
                    break;
                case "individual":
                    q = q.Where(u => !u.IsPlatformStaff
                                  && !u.OrganizationMemberships.Any());
                    break;
                // anything else: ignored
            }
        }

        var total = await q.CountAsync(ct);

        // Materialize the page first, then derive UserType client-side. EF
        // would otherwise emit a CASE-WHEN with two extra subqueries per row;
        // a single Any() check on the materialized list is cheaper and
        // reads better.
        var rows = await q
            .OrderByDescending(u => u.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new
            {
                u.Id,
                u.FullName,
                u.Email,
                u.IsPlatformStaff,
                HasMembership = u.OrganizationMemberships.Any(),
                Status = u.Status.ToString(),
                u.EmailVerified,
                CountryNameAr = u.Country != null ? u.Country.NameAr : null,
                u.CreatedAt,
            })
            .ToListAsync(ct);

        var userIds = rows.Select(r => r.Id).ToList();
        var memberships = await _db.OrganizationMembers
            .AsNoTracking()
            .Where(m => userIds.Contains(m.UserId) && m.IsActive)
            .OrderBy(m => m.JoinedAt)
            .Select(m => new
            {
                m.UserId,
                m.OrganizationId,
                NameAr = m.Organization.NameAr,
                m.Organization.Slug,
                RoleName = m.Role.Name,
            })
            .ToListAsync(ct);

        var primaryByUser = memberships
            .GroupBy(m => m.UserId)
            .ToDictionary(g => g.Key, g => g.First());
        var countByUser = memberships
            .GroupBy(m => m.UserId)
            .ToDictionary(g => g.Key, g => g.Count());

        var items = rows.Select(r =>
        {
            primaryByUser.TryGetValue(r.Id, out var primary);
            countByUser.TryGetValue(r.Id, out var orgCount);
            return new AdminUserListItemDto(
                r.Id,
                r.FullName,
                r.Email,
                UserType: DeriveUserType(r.IsPlatformStaff, r.HasMembership),
                r.Status,
                r.EmailVerified,
                r.CountryNameAr,
                r.CreatedAt,
                primary?.OrganizationId,
                primary?.NameAr,
                primary?.Slug,
                primary?.RoleName,
                orgCount);
        }).ToList();

        return new PagedResult<AdminUserListItemDto>(items, total, page, pageSize);
    }

    public async Task<AdminUserDetailDto> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var u = await _db.Users
            .AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => new
            {
                x.Id, x.FullName, x.Email, x.Phone,
                x.IsPlatformStaff,
                Status = x.Status.ToString(),
                x.EmailVerified, x.PhoneVerified,
                x.PreferredLanguage, x.JobTitle,
                x.CountryId,
                CountryNameAr = x.Country != null ? x.Country.NameAr : null,
                x.CreatedAt, x.LockoutEndsAt,
                HasMembership = x.OrganizationMemberships.Any(),
                UploadedReportsCount = x.UploadedReports.Count,
            })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("User not found.");

        var orgs = await _db.OrganizationMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.JoinedAt)
            .Select(m => new AdminUserOrgMembershipDto(
                m.OrganizationId,
                m.Organization.NameAr,
                m.Organization.Slug,
                m.Organization.Status.ToString(),
                m.Role.Name,
                m.JoinedAt,
                m.IsActive))
            .ToListAsync(ct);

        return new AdminUserDetailDto(
            u.Id, u.FullName, u.Email, u.Phone,
            UserType: DeriveUserType(u.IsPlatformStaff, u.HasMembership),
            u.Status, u.EmailVerified, u.PhoneVerified, u.IsPlatformStaff,
            u.PreferredLanguage, u.JobTitle, u.CountryId, u.CountryNameAr,
            u.CreatedAt, u.LockoutEndsAt, u.UploadedReportsCount, orgs);
    }

    public async Task<AdminUserDetailDto> BanAsync(
        Guid actingUserId, Guid targetUserId, BanUserRequest req, CancellationToken ct = default)
    {
        if (actingUserId == targetUserId)
            throw new InvalidOperationException("You cannot ban your own account.");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == targetUserId, ct)
            ?? throw new KeyNotFoundException("User not found.");

        // Staff bans go through /api/admin/staff so the SuperAdmin guard
        // (last-staff-standing) and 2FA reset live in one place.
        if (user.IsPlatformStaff)
            throw new InvalidOperationException(
                "Use staff management to manage platform staff accounts.");

        var prev = user.Status;
        user.Status = UserStatus.Suspended;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "user.ban",
            targetEntityType: "User",
            targetEntityId: targetUserId,
            reason: req.Reason,
            beforeState: new { Status = prev.ToString() },
            afterState: new { Status = user.Status.ToString() },
            ct: ct);

        return await GetAsync(targetUserId, ct);
    }

    public async Task<AdminUserDetailDto> UnbanAsync(
        Guid actingUserId, Guid targetUserId, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == targetUserId, ct)
            ?? throw new KeyNotFoundException("User not found.");

        if (user.IsPlatformStaff)
            throw new InvalidOperationException(
                "Use staff management to manage platform staff accounts.");

        var prev = user.Status;
        user.Status = UserStatus.Active;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "user.unban",
            targetEntityType: "User",
            targetEntityId: targetUserId,
            beforeState: new { Status = prev.ToString() },
            afterState: new { Status = user.Status.ToString() },
            ct: ct);

        return await GetAsync(targetUserId, ct);
    }

    public async Task DeleteAsync(Guid actingUserId, Guid targetUserId, CancellationToken ct = default)
    {
        if (actingUserId == targetUserId)
            throw new InvalidOperationException("You cannot delete your own account.");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == targetUserId, ct)
            ?? throw new KeyNotFoundException("User not found.");

        if (user.IsPlatformStaff)
            throw new InvalidOperationException(
                "Use staff management to delete platform staff accounts.");

        // Owner-of-record guard: orgs track who originally registered them
        // via Organization.CreatedByUserId. Refuse to delete a user who is
        // still that owner — the platform owner needs to first transfer the
        // org or hard-delete it.
        var ownsOrgs = await _db.Organizations
            .AsNoTracking()
            .AnyAsync(o => o.CreatedByUserId == targetUserId, ct);
        if (ownsOrgs)
            throw new InvalidOperationException(
                "Cannot delete a user who is the owner-of-record of an organization. Reassign or delete the org first.");

        user.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(
            adminUserId: actingUserId,
            actionType: "user.delete",
            targetEntityType: "User",
            targetEntityId: targetUserId,
            beforeState: new { user.Email, user.FullName, Status = user.Status.ToString() },
            ct: ct);
    }

    public async Task<PagedResult<AdminUserReportItemDto>> ListReportsAsync(
        Guid userId, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var q = _db.Reports.AsNoTracking().Where(r => r.UploadedByUserId == userId);

        var total = await q.CountAsync(ct);

        var rows = await q
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new AdminUserReportItemDto(
                r.Id,
                r.TitleAr,
                r.TitleEn,
                r.Status.ToString(),
                r.OrganizationId,
                r.Organization.NameAr,
                r.CreatedAt,
                r.PublishedAt))
            .ToListAsync(ct);

        return new PagedResult<AdminUserReportItemDto>(rows, total, page, pageSize);
    }

    private static string DeriveUserType(bool isPlatformStaff, bool hasMembership)
    {
        if (isPlatformStaff) return "staff";
        if (hasMembership) return "orgMember";
        return "individual";
    }
}
