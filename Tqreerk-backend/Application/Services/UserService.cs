using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Users;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Entities;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.Application.Services;

public class UserService : IUserService
{
    private readonly TaqreerkDbContext _db;

    public UserService(TaqreerkDbContext db) => _db = db;

    public async Task<UserProfileDto> GetProfileAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("User not found.");

        // A user is in at most one organization for now (per current product scope).
        // We surface the first active membership; multi-org support can extend this later.
        var membership = await _db.OrganizationMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.IsActive)
            .Select(m => new
            {
                Org = m.Organization,
                RoleName = m.Role.Name,
            })
            .FirstOrDefaultAsync(ct);

        OrganizationSummaryDto? orgDto = null;
        if (membership is not null && membership.Org is not null)
        {
            var o = membership.Org;
            // The wizard guard depends on this — only the founder is bounced into
            // the wizard on incomplete orgs; invited members go straight to dashboard.
            // For legacy orgs without CreatedByUserId, fall back to the earliest active member
            // so founder protection still has an anchor.
            Guid? founderId = o.CreatedByUserId;
            if (!founderId.HasValue)
            {
                founderId = await _db.OrganizationMembers
                    .AsNoTracking()
                    .Where(m => m.OrganizationId == o.Id && m.IsActive)
                    .OrderBy(m => m.JoinedAt)
                    .ThenBy(m => m.Id)
                    .Select(m => (Guid?)m.UserId)
                    .FirstOrDefaultAsync(ct);
            }
            var isFounder = founderId.HasValue && founderId.Value == userId;
            orgDto = new OrganizationSummaryDto(
                o.Id, o.NameAr, o.NameEn, o.Slug, o.Status.ToString(),
                o.IsVerified, o.LogoUrl, membership.RoleName, isFounder);
        }

        return ToDto(user, orgDto);
    }

    public async Task<UserProfileDto> UpdateProfileAsync(Guid userId, UpdateProfileRequest req, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("User not found.");

        if (string.IsNullOrWhiteSpace(req.FullName))
            throw new ArgumentException("Full name is required.");

        user.FullName = req.FullName.Trim();
        user.JobTitle = req.JobTitle?.Trim();
        user.InterestField = req.InterestField?.Trim();
        user.CountryId = req.CountryId;

        if (!string.IsNullOrWhiteSpace(req.PreferredLanguage))
            user.PreferredLanguage = req.PreferredLanguage.Trim();

        // Phone changes reset the verification flag; users must re-verify.
        if (req.Phone is not null && req.Phone != user.Phone)
        {
            user.Phone = string.IsNullOrWhiteSpace(req.Phone) ? null : req.Phone.Trim();
            user.PhoneVerified = false;
        }

        await _db.SaveChangesAsync(ct);
        // UpdateProfile doesn't change membership; reuse the rich loader so the
        // response shape stays consistent with GetProfileAsync.
        return await GetProfileAsync(userId, ct);
    }

    public async Task<UserInterestsDto> GetInterestsAsync(Guid userId, CancellationToken ct = default)
    {
        var rows = await _db.UserInterests
            .AsNoTracking()
            .Where(i => i.UserId == userId)
            .ToListAsync(ct);

        return Group(rows);
    }

    public async Task<UserInterestsDto> SetInterestsAsync(Guid userId, SetInterestsRequest req, CancellationToken ct = default)
    {
        var userExists = await _db.Users.AnyAsync(u => u.Id == userId, ct);
        if (!userExists) throw new KeyNotFoundException("User not found.");

        var sectorIds = (req.SectorIds ?? []).Distinct().ToList();
        var organizationIds = (req.OrganizationIds ?? []).Distinct().ToList();
        var countryIds = (req.CountryIds ?? []).Distinct().ToList();

        await EnsureAllExistAsync(_db.Sectors, sectorIds, "Sector", ct);
        await EnsureAllExistAsync(_db.Organizations, organizationIds, "Organization", ct);
        await EnsureAllExistAsync(_db.Countries, countryIds, "Country", ct);

        // Replace semantics: drop all then re-insert. Simple and correct for a
        // small per-user set (no churn on unchanged entries at scale).
        var existing = await _db.UserInterests.Where(i => i.UserId == userId).ToListAsync(ct);
        _db.UserInterests.RemoveRange(existing);

        foreach (var id in sectorIds)
            _db.UserInterests.Add(new UserInterest { UserId = userId, SectorId = id });
        foreach (var id in organizationIds)
            _db.UserInterests.Add(new UserInterest { UserId = userId, OrganizationId = id });
        foreach (var id in countryIds)
            _db.UserInterests.Add(new UserInterest { UserId = userId, CountryId = id });

        await _db.SaveChangesAsync(ct);

        return new UserInterestsDto(sectorIds, organizationIds, countryIds);
    }

    private static async Task EnsureAllExistAsync<T>(IQueryable<T> source, IReadOnlyList<Guid> ids, string label, CancellationToken ct)
        where T : Domain.Common.BaseEntity
    {
        if (ids.Count == 0) return;

        var found = await source.Where(e => ids.Contains(e.Id)).Select(e => e.Id).ToListAsync(ct);
        var missing = ids.Except(found).ToList();
        if (missing.Count > 0)
            throw new ArgumentException($"Unknown {label} IDs: {string.Join(", ", missing)}");
    }

    private static UserInterestsDto Group(IEnumerable<UserInterest> rows) => new(
        rows.Where(i => i.SectorId is not null).Select(i => i.SectorId!.Value).ToList(),
        rows.Where(i => i.OrganizationId is not null).Select(i => i.OrganizationId!.Value).ToList(),
        rows.Where(i => i.CountryId is not null).Select(i => i.CountryId!.Value).ToList()
    );

    private static UserProfileDto ToDto(User u, OrganizationSummaryDto? org = null) => new(
        u.Id, u.Email, u.Phone, u.FullName, u.UserType, u.JobTitle, u.InterestField,
        u.CountryId, u.EmailVerified, u.PhoneVerified, u.PreferredLanguage, u.CreatedAt, org
    );
}
