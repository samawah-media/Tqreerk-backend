using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Organizations;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Settings;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;
using Microsoft.Extensions.Options;

namespace Taqreerk.Application.Services;

public class OrganizationService : IOrganizationService
{
    private const string CommercialRegisterFileType = "commercial_register";
    private static readonly string[] AllowedContentTypes =
    [
        "application/pdf",
        "image/png",
        "image/jpeg",
        "image/jpg",
    ];
    private const long MaxFileSizeBytes = 5 * 1024 * 1024;
    private const string OrgFileFolder = "organizations";

    private static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);

    private readonly TaqreerkDbContext _db;
    private readonly IFileStorage _files;
    private readonly IEmailSender _email;
    private readonly EmailSettings _emailSettings;

    public OrganizationService(
        TaqreerkDbContext db,
        IFileStorage files,
        IEmailSender email,
        IOptions<EmailSettings> emailSettings)
    {
        _db = db;
        _files = files;
        _email = email;
        _emailSettings = emailSettings.Value;
    }

    public async Task<OrganizationDetailDto> GetMineAsync(Guid userId, CancellationToken ct = default)
    {
        var org = await LoadOrgForUserAsync(userId, ct);
        return await ToDtoAsync(org, ct);
    }

    public async Task<OrganizationDetailDto> UpdateBasicsAsync(Guid userId, UpdateOrganizationBasicsRequest req, CancellationToken ct = default)
    {
        var org = await LoadOrgForUserAsync(userId, ct);

        if (req.CountryId.HasValue)
        {
            // Validate FK; otherwise EF will throw a confusing constraint error.
            var exists = await _db.Countries.AnyAsync(c => c.Id == req.CountryId.Value, ct);
            if (!exists) throw new ArgumentException("Unknown country.");
            org.CountryId = req.CountryId;
        }

        if (req.City is not null) org.City = string.IsNullOrWhiteSpace(req.City) ? null : req.City.Trim();
        if (req.Phone is not null) org.Phone = string.IsNullOrWhiteSpace(req.Phone) ? null : req.Phone.Trim();

        if (req.CommercialRegisterNo is not null)
        {
            var profile = EnsureProfile(org);
            profile.CommercialRegisterNo = string.IsNullOrWhiteSpace(req.CommercialRegisterNo)
                ? null : req.CommercialRegisterNo.Trim();
        }

        await _db.SaveChangesAsync(ct);
        await MaybeActivateAsync(org, ct);
        return await ToDtoAsync(org, ct);
    }

    public async Task<OrganizationDetailDto> UpdateScopeAsync(Guid userId, UpdateOrganizationScopeRequest req, CancellationToken ct = default)
    {
        var org = await LoadOrgForUserAsync(userId, ct);

        if (req.Type.HasValue) org.Type = req.Type.Value;
        if (req.SectorScope is not null) org.SectorScope = string.IsNullOrWhiteSpace(req.SectorScope) ? null : req.SectorScope.Trim();
        if (req.WebsiteUrl is not null) org.WebsiteUrl = string.IsNullOrWhiteSpace(req.WebsiteUrl) ? null : req.WebsiteUrl.Trim();
        if (req.Description is not null) org.Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim();

        await _db.SaveChangesAsync(ct);
        await MaybeActivateAsync(org, ct);
        return await ToDtoAsync(org, ct);
    }

    public async Task<OrganizationDetailDto> UpdateReportsAsync(Guid userId, UpdateOrganizationReportsRequest req, CancellationToken ct = default)
    {
        var org = await LoadOrgForUserAsync(userId, ct);
        var profile = EnsureProfile(org);

        if (req.IssuesReports.HasValue) profile.IssuesReports = req.IssuesReports.Value;
        if (req.AnnualReportsCount.HasValue) profile.AnnualReportsCount = req.AnnualReportsCount.Value;
        if (req.WantsToPublish.HasValue) profile.WantsToPublish = req.WantsToPublish.Value;
        if (req.InterestedInSubscription.HasValue) profile.InterestedInSubscription = req.InterestedInSubscription.Value;

        await _db.SaveChangesAsync(ct);
        await MaybeActivateAsync(org, ct);
        return await ToDtoAsync(org, ct);
    }

    public async Task<OrganizationDetailDto> UpdateContactAsync(Guid userId, UpdateOrganizationContactRequest req, CancellationToken ct = default)
    {
        var org = await LoadOrgForUserAsync(userId, ct);
        var profile = EnsureProfile(org);

        if (req.ContactPersonName is not null) profile.ContactPersonName = string.IsNullOrWhiteSpace(req.ContactPersonName) ? null : req.ContactPersonName.Trim();
        if (req.ContactPersonTitle is not null) profile.ContactPersonTitle = string.IsNullOrWhiteSpace(req.ContactPersonTitle) ? null : req.ContactPersonTitle.Trim();
        if (req.ContactEmail is not null) profile.ContactEmail = string.IsNullOrWhiteSpace(req.ContactEmail) ? null : req.ContactEmail.Trim();

        if (req.PoliciesAccepted == true && !profile.PoliciesAccepted)
        {
            profile.PoliciesAccepted = true;
            profile.PoliciesAcceptedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
        await MaybeActivateAsync(org, ct);
        return await ToDtoAsync(org, ct);
    }

    public async Task<OrganizationFileDto> UploadFileAsync(
        Guid userId,
        string fileType,
        Stream content,
        string originalFileName,
        string contentType,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(fileType))
            throw new ArgumentException("File type is required.");

        if (!AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Only PDF, PNG, and JPEG files are accepted.");

        // Length check: stream may report -1 if not seekable; in practice multipart sections are seekable on Kestrel.
        if (content.CanSeek && content.Length > MaxFileSizeBytes)
            throw new ArgumentException("File exceeds the 5 MB size limit.");

        var org = await LoadOrgForUserAsync(userId, ct);

        // Replace any prior file of the same type to avoid orphaned blobs.
        var existing = await _db.OrganizationFiles
            .Where(f => f.OrganizationId == org.Id && f.FileType == fileType)
            .ToListAsync(ct);

        var stored = await _files.UploadAsync(content, originalFileName, contentType, $"{OrgFileFolder}/{org.Id}", ct);

        foreach (var prev in existing)
        {
            try { await _files.DeleteAsync(prev.FileUrl, ct); }
            catch { /* best-effort: don't block re-upload on delete failure */ }
            _db.OrganizationFiles.Remove(prev);
        }

        var entity = new OrganizationFile
        {
            OrganizationId = org.Id,
            FileType = fileType,
            FileUrl = stored.ObjectKey,
            UploadedBy = userId,
        };
        _db.OrganizationFiles.Add(entity);

        // If this is the commercial register, also stamp the profile so the wizard
        // shows the file alongside CR number without an extra round-trip.
        if (fileType == CommercialRegisterFileType)
        {
            var profile = EnsureProfile(org);
            profile.LicenseDocumentUrl = stored.ObjectKey;
        }

        await _db.SaveChangesAsync(ct);
        await MaybeActivateAsync(org, ct);

        var url = await _files.GetReadUrlAsync(entity.FileUrl, ct: ct);
        return new OrganizationFileDto(entity.Id, entity.FileType, url, entity.CreatedAt);
    }

    public async Task<IReadOnlyList<CountryDto>> ListCountriesAsync(CancellationToken ct = default)
    {
        return await _db.Countries.AsNoTracking()
            .OrderBy(c => c.NameAr)
            .Select(c => new CountryDto(c.Id, c.NameAr, c.NameEn, c.IsoCode))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<SectorDto>> ListSectorsAsync(CancellationToken ct = default)
    {
        return await _db.Sectors.AsNoTracking()
            .Where(s => s.IsActive)
            .OrderBy(s => s.NameAr)
            .Select(s => new SectorDto(s.Id, s.NameAr, s.NameEn, s.Slug))
            .ToListAsync(ct);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<Organization> LoadOrgForUserAsync(Guid userId, CancellationToken ct)
    {
        var membership = await _db.OrganizationMembers
            .Include(m => m.Organization).ThenInclude(o => o.Profile)
            .Where(m => m.UserId == userId)
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("No organization is associated with this user.");

        return membership.Organization;
    }

    private OrganizationProfile EnsureProfile(Organization org)
    {
        if (org.Profile is null)
        {
            org.Profile = new OrganizationProfile();
            // EF will set the FK from the relationship when SaveChangesAsync runs.
        }
        return org.Profile;
    }

    /// Activate the org once the wizard's required fields are all present.
    /// Required-for-completion: country, type, contact name, policies accepted, CR document.
    private async Task MaybeActivateAsync(Organization org, CancellationToken ct)
    {
        if (org.Status == OrganizationStatus.Active) return;

        var profile = org.Profile;
        var hasCommercialRegisterFile = !string.IsNullOrWhiteSpace(profile?.LicenseDocumentUrl);
        var requiredOk =
            org.CountryId is not null
            && profile is not null
            && profile.PoliciesAccepted
            && !string.IsNullOrWhiteSpace(profile.ContactPersonName)
            && hasCommercialRegisterFile;

        if (requiredOk)
        {
            org.Status = OrganizationStatus.Active;
            await _db.SaveChangesAsync(ct);
        }
    }

    private async Task<OrganizationDetailDto> ToDtoAsync(Organization org, CancellationToken ct)
    {
        // Reload the file for this org's commercial register, if any.
        var crFile = await _db.OrganizationFiles
            .Where(f => f.OrganizationId == org.Id && f.FileType == CommercialRegisterFileType)
            .OrderByDescending(f => f.CreatedAt)
            .FirstOrDefaultAsync(ct);

        OrganizationFileDto? crDto = null;
        if (crFile is not null)
        {
            var url = await _files.GetReadUrlAsync(crFile.FileUrl, ct: ct);
            crDto = new OrganizationFileDto(crFile.Id, crFile.FileType, url, crFile.CreatedAt);
        }

        OrganizationProfileDto? profileDto = null;
        if (org.Profile is not null)
        {
            var p = org.Profile;
            profileDto = new OrganizationProfileDto(
                p.CommercialRegisterNo, p.LicenseDocumentUrl, p.IssuesReports, p.AnnualReportsCount,
                p.WantsToPublish, p.InterestedInSubscription, p.ContactPersonName, p.ContactPersonTitle,
                p.ContactEmail, p.PoliciesAccepted, p.PoliciesAcceptedAt);
        }

        var profileComplete = org.Status == OrganizationStatus.Active;

        return new OrganizationDetailDto(
            org.Id, org.NameAr, org.NameEn, org.Slug, org.Type.ToString(), org.Status.ToString(),
            org.IsVerified, org.IsPartner, org.CountryId, org.City, org.Phone, org.WebsiteUrl,
            org.SectorScope, org.LogoUrl, org.Description, profileDto, crDto, profileComplete, org.CreatedAt
        );
    }

    // ── Members ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<OrganizationMemberDto>> ListMembersAsync(Guid currentUserId, CancellationToken ct = default)
    {
        var orgId = await GetOrgIdForUserAsync(currentUserId, ct);
        var founderId = await ResolveFounderIdAsync(orgId, ct);

        var rows = await _db.OrganizationMembers
            .AsNoTracking()
            .Where(m => m.OrganizationId == orgId && m.IsActive)
            .Select(m => new
            {
                m.UserId,
                m.User.FullName,
                m.User.Email,
                m.IsActive,
                m.JoinedAt,
            })
            .OrderBy(m => m.JoinedAt)
            .ToListAsync(ct);

        return rows.Select(m => new OrganizationMemberDto(
            m.UserId, m.FullName, m.Email, m.IsActive,
            IsFounder: founderId.HasValue && founderId.Value == m.UserId,
            IsCurrentUser: m.UserId == currentUserId,
            m.JoinedAt
        )).ToList();
    }

    public async Task RemoveMemberAsync(Guid currentUserId, Guid targetUserId, string? ipAddress, CancellationToken ct = default)
    {
        var orgId = await GetOrgIdForUserAsync(currentUserId, ct);
        var founderId = await ResolveFounderIdAsync(orgId, ct);

        if (founderId.HasValue && founderId.Value == targetUserId)
            throw new InvalidOperationException("The organization founder cannot be removed.");

        // Always keep at least one active member so the org stays reachable.
        var activeCount = await _db.OrganizationMembers
            .CountAsync(m => m.OrganizationId == orgId && m.IsActive, ct);
        if (activeCount <= 1)
            throw new InvalidOperationException("Cannot remove the last remaining member.");

        var membership = await _db.OrganizationMembers
            .FirstOrDefaultAsync(m => m.OrganizationId == orgId && m.UserId == targetUserId && m.IsActive, ct)
            ?? throw new KeyNotFoundException("Member not found in this organization.");

        membership.IsActive = false;
        await WriteAuditAsync(orgId, currentUserId, "member.removed", "OrganizationMember", membership.Id,
            new { removedUserId = targetUserId }, ipAddress, ct);

        await _db.SaveChangesAsync(ct);
    }

    /// Returns the org's founder id. Falls back to the earliest active member when
    /// CreatedByUserId is NULL (true for orgs registered before Feature 5's migration).
    /// This way founder protection always has *something* to anchor on.
    private async Task<Guid?> ResolveFounderIdAsync(Guid orgId, CancellationToken ct)
    {
        var explicitFounder = await _db.Organizations
            .AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => o.CreatedByUserId)
            .FirstOrDefaultAsync(ct);

        if (explicitFounder.HasValue) return explicitFounder;

        // Fallback: the oldest active membership row. Stable, deterministic, and matches
        // what the backfill migration would write.
        return await _db.OrganizationMembers
            .AsNoTracking()
            .Where(m => m.OrganizationId == orgId && m.IsActive)
            .OrderBy(m => m.JoinedAt)
            .ThenBy(m => m.Id)
            .Select(m => (Guid?)m.UserId)
            .FirstOrDefaultAsync(ct);
    }

    // ── Invitations ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<OrganizationInvitationDto>> ListInvitationsAsync(Guid currentUserId, CancellationToken ct = default)
    {
        var orgId = await GetOrgIdForUserAsync(currentUserId, ct);
        var now = DateTime.UtcNow;

        // Mark expired pending invites lazily on read so the dashboard reflects truth
        // without a background job. Cheaper than a periodic sweep at this scale.
        var stale = await _db.OrganizationInvitations
            .Where(i => i.OrganizationId == orgId && i.Status == InvitationStatus.Pending && i.ExpiresAt < now)
            .ToListAsync(ct);
        if (stale.Count > 0)
        {
            foreach (var inv in stale) inv.Status = InvitationStatus.Expired;
            await _db.SaveChangesAsync(ct);
        }

        return await _db.OrganizationInvitations
            .AsNoTracking()
            .Where(i => i.OrganizationId == orgId && i.Status == InvitationStatus.Pending)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new OrganizationInvitationDto(
                i.Id, i.Email, i.Status.ToString(), i.ExpiresAt, i.CreatedAt,
                i.InvitedByUser.FullName))
            .ToListAsync(ct);
    }

    public async Task<OrganizationInvitationDto> CreateInvitationAsync(Guid currentUserId, string email, string? ipAddress, CancellationToken ct = default)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var orgId = await GetOrgIdForUserAsync(currentUserId, ct);

        // Already a member? Block the invite — they're already in.
        var alreadyMember = await _db.OrganizationMembers
            .Include(m => m.User)
            .Where(m => m.OrganizationId == orgId && m.IsActive && m.User.Email == normalized)
            .AnyAsync(ct);
        if (alreadyMember)
            throw new InvalidOperationException("This email is already a member of the organization.");

        // Already has a pending invite? Refuse rather than spam them.
        var hasPending = await _db.OrganizationInvitations
            .AnyAsync(i => i.OrganizationId == orgId && i.Email == normalized && i.Status == InvitationStatus.Pending, ct);
        if (hasPending)
            throw new InvalidOperationException("An invitation is already pending for this email.");

        var rawToken = GenerateToken();
        var invitation = new OrganizationInvitation
        {
            OrganizationId = orgId,
            Email = normalized,
            TokenHash = HashToken(rawToken),
            InvitedByUserId = currentUserId,
            Status = InvitationStatus.Pending,
            ExpiresAt = DateTime.UtcNow.Add(InvitationLifetime),
        };
        _db.OrganizationInvitations.Add(invitation);

        await WriteAuditAsync(orgId, currentUserId, "invitation.sent", "OrganizationInvitation", invitation.Id,
            new { email = normalized }, ipAddress, ct);

        await _db.SaveChangesAsync(ct);

        // Send the invitation email. Failure logs but doesn't roll back the row —
        // the OrgAdmin can resend (we'll surface the row in the pending list and add
        // a resend button later if needed).
        try
        {
            var org = await _db.Organizations.AsNoTracking().FirstAsync(o => o.Id == orgId, ct);
            var inviter = await _db.Users.AsNoTracking().FirstAsync(u => u.Id == currentUserId, ct);
            var link = $"{_emailSettings.AppBaseUrl.TrimEnd('/')}/invitations/accept?token={Uri.EscapeDataString(rawToken)}";
            var body =
                $"<p>Hello,</p>" +
                $"<p>{System.Net.WebUtility.HtmlEncode(inviter.FullName)} has invited you to join " +
                $"<strong>{System.Net.WebUtility.HtmlEncode(org.NameAr)}</strong> on Taqreerk.</p>" +
                $"<p>Click the link below to accept (expires in 7 days):</p>" +
                $"<p><a href=\"{link}\">{link}</a></p>";
            await _email.SendEmailAsync(normalized, "You've been invited to join an organization on Taqreerk", body, ct);
        }
        catch
        {
            // Swallowed — the invite row exists; org admin can cancel + recreate if needed.
        }

        // Reload with the inviter name for the response shape parity with the list endpoint.
        var inviterName = await _db.Users.AsNoTracking().Where(u => u.Id == currentUserId).Select(u => u.FullName).FirstAsync(ct);
        return new OrganizationInvitationDto(
            invitation.Id, invitation.Email, invitation.Status.ToString(),
            invitation.ExpiresAt, invitation.CreatedAt, inviterName);
    }

    public async Task CancelInvitationAsync(Guid currentUserId, Guid invitationId, string? ipAddress, CancellationToken ct = default)
    {
        var orgId = await GetOrgIdForUserAsync(currentUserId, ct);

        var invitation = await _db.OrganizationInvitations
            .FirstOrDefaultAsync(i => i.Id == invitationId && i.OrganizationId == orgId, ct)
            ?? throw new KeyNotFoundException("Invitation not found.");

        if (invitation.Status != InvitationStatus.Pending)
            throw new InvalidOperationException("Only pending invitations can be cancelled.");

        invitation.Status = InvitationStatus.Cancelled;

        await WriteAuditAsync(orgId, currentUserId, "invitation.cancelled", "OrganizationInvitation", invitation.Id,
            new { email = invitation.Email }, ipAddress, ct);

        await _db.SaveChangesAsync(ct);
    }

    public async Task<InvitationPreviewDto> PreviewInvitationAsync(string rawToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
            throw new ArgumentException("Token is required.");

        var hash = HashToken(rawToken);
        var invitation = await _db.OrganizationInvitations
            .Include(i => i.Organization)
            .Include(i => i.InvitedByUser)
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.TokenHash == hash, ct)
            ?? throw new KeyNotFoundException("Invitation not found.");

        var isExpired = invitation.Status == InvitationStatus.Pending
            && invitation.ExpiresAt < DateTime.UtcNow;

        return new InvitationPreviewDto(
            invitation.Organization.NameAr,
            invitation.Organization.NameEn,
            invitation.Email,
            invitation.InvitedByUser?.FullName,
            invitation.ExpiresAt,
            isExpired,
            invitation.Status.ToString()
        );
    }

    public async Task AcceptInvitationAsync(Guid acceptingUserId, string rawToken, string? ipAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
            throw new ArgumentException("Token is required.");

        var hash = HashToken(rawToken);
        var invitation = await _db.OrganizationInvitations
            .FirstOrDefaultAsync(i => i.TokenHash == hash, ct)
            ?? throw new KeyNotFoundException("Invitation not found.");

        if (invitation.Status == InvitationStatus.Cancelled)
            throw new InvalidOperationException("This invitation has been cancelled.");
        if (invitation.Status == InvitationStatus.Accepted)
            throw new InvalidOperationException("This invitation has already been accepted.");

        if (invitation.ExpiresAt < DateTime.UtcNow)
        {
            invitation.Status = InvitationStatus.Expired;
            await _db.SaveChangesAsync(ct);
            throw new InvalidOperationException("This invitation has expired.");
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == acceptingUserId, ct)
            ?? throw new UnauthorizedAccessException("User not found.");

        // Email match: prevent another logged-in user from accepting someone else's invite.
        if (!string.Equals(user.Email, invitation.Email, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("This invitation was issued to a different email.");

        // Already a member? Make the accept idempotent — flip the invite to accepted, but
        // don't insert a duplicate membership row.
        var existingMembership = await _db.OrganizationMembers
            .FirstOrDefaultAsync(m => m.OrganizationId == invitation.OrganizationId && m.UserId == user.Id, ct);

        if (existingMembership is null)
        {
            var memberRole = await _db.Roles.FirstOrDefaultAsync(r => r.Name == "admin", ct)
                ?? throw new InvalidOperationException("Default roles not seeded.");

            _db.OrganizationMembers.Add(new OrganizationMember
            {
                OrganizationId = invitation.OrganizationId,
                UserId = user.Id,
                RoleId = memberRole.Id,
                IsActive = true,
            });
        }
        else if (!existingMembership.IsActive)
        {
            // Re-activate someone who was previously removed.
            existingMembership.IsActive = true;
        }

        invitation.Status = InvitationStatus.Accepted;
        invitation.AcceptedAt = DateTime.UtcNow;
        invitation.AcceptedByUserId = user.Id;

        await WriteAuditAsync(invitation.OrganizationId, user.Id, "member.joined", "OrganizationMember", null,
            new { invitationId = invitation.Id, email = invitation.Email }, ipAddress, ct);

        await _db.SaveChangesAsync(ct);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<Guid> GetOrgIdForUserAsync(Guid userId, CancellationToken ct)
    {
        var orgId = await _db.OrganizationMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId && m.IsActive)
            .Select(m => m.OrganizationId)
            .FirstOrDefaultAsync(ct);

        if (orgId == Guid.Empty)
            throw new KeyNotFoundException("No organization is associated with this user.");

        return orgId;
    }

    private async Task WriteAuditAsync(
        Guid orgId, Guid userId, string eventType, string entityType, Guid? entityId,
        object? payload, string? ipAddress, CancellationToken ct)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            OrganizationId = orgId,
            UserId = userId,
            EventType = eventType,
            EntityType = entityType,
            EntityId = entityId,
            IpAddress = ipAddress,
            Payload = payload is null ? null : JsonSerializer.Serialize(payload),
        });
        // Caller is responsible for SaveChanges.
        await Task.CompletedTask;
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(48);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
