using Taqreerk.Application.DTOs.Organizations;

namespace Taqreerk.Application.Interfaces;

public interface IOrganizationService
{
    Task<OrganizationDetailDto> GetMineAsync(Guid userId, CancellationToken ct = default);

    Task<OrganizationDetailDto> UpdateNamesAsync(Guid userId, UpdateOrganizationNamesRequest req, CancellationToken ct = default);
    Task<OrganizationDetailDto> UpdateBasicsAsync(Guid userId, UpdateOrganizationBasicsRequest req, CancellationToken ct = default);
    Task<OrganizationDetailDto> UpdateScopeAsync(Guid userId, UpdateOrganizationScopeRequest req, CancellationToken ct = default);
    Task<OrganizationDetailDto> UpdateReportsAsync(Guid userId, UpdateOrganizationReportsRequest req, CancellationToken ct = default);
    Task<OrganizationDetailDto> UpdateContactAsync(Guid userId, UpdateOrganizationContactRequest req, CancellationToken ct = default);

    /// Persist an uploaded file as an OrganizationFile of the given type
    /// (typically "commercial_register"). Replaces any prior file of the same type.
    Task<OrganizationFileDto> UploadFileAsync(
        Guid userId,
        string fileType,
        Stream content,
        string originalFileName,
        string contentType,
        CancellationToken ct = default);

    /// List public reference data (cached at the controller level if needed).
    Task<IReadOnlyList<CountryDto>> ListCountriesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<SectorDto>> ListSectorsAsync(CancellationToken ct = default);

    // ── Members & invitations ────────────────────────────────────────────────

    Task<IReadOnlyList<OrganizationMemberDto>> ListMembersAsync(Guid currentUserId, CancellationToken ct = default);
    Task RemoveMemberAsync(Guid currentUserId, Guid targetUserId, string? ipAddress, CancellationToken ct = default);

    /// Change an org member's role. Founder-only. The founder's own role
    /// is immutable to keep ownership stable.
    Task<OrganizationMemberDto> ChangeMemberRoleAsync(
        Guid currentUserId, Guid targetUserId, string roleName, string? ipAddress, CancellationToken ct = default);

    Task<IReadOnlyList<OrganizationInvitationDto>> ListInvitationsAsync(Guid currentUserId, CancellationToken ct = default);
    Task<OrganizationInvitationDto> CreateInvitationAsync(Guid currentUserId, string email, string? ipAddress, CancellationToken ct = default);
    Task CancelInvitationAsync(Guid currentUserId, Guid invitationId, string? ipAddress, CancellationToken ct = default);

    /// Public lookup by raw token. Anonymous — used by the accept page so the
    /// invitee can see what they're accepting before logging in.
    Task<InvitationPreviewDto> PreviewInvitationAsync(string rawToken, CancellationToken ct = default);

    /// Acceptance: the *currently authenticated* user is added to the org.
    /// Email match against the invite is enforced.
    Task AcceptInvitationAsync(Guid acceptingUserId, string rawToken, string? ipAddress, CancellationToken ct = default);
}
