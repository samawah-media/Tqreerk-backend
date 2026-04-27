using Taqreerk.Domain.Common;
using Taqreerk.Domain.Enums;

namespace Taqreerk.Domain.Entities;

public class OrganizationInvitation : BaseEntity
{
    public Guid OrganizationId { get; set; }
    public string Email { get; set; } = string.Empty;

    /// SHA-256 hash of the raw token. The plaintext token only ever lives in the
    /// invitation email; we look it up by hashing what the acceptor presents.
    public string TokenHash { get; set; } = string.Empty;

    public Guid InvitedByUserId { get; set; }
    public InvitationStatus Status { get; set; } = InvitationStatus.Pending;
    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public Guid? AcceptedByUserId { get; set; }

    public Organization Organization { get; set; } = null!;
    public User InvitedByUser { get; set; } = null!;
}
