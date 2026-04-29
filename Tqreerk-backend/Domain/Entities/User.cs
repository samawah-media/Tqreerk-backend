using Taqreerk.Domain.Common;
using Taqreerk.Domain.Enums;

namespace Taqreerk.Domain.Entities;

public class User : SoftDeletableEntity
{
    public string Email { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string PasswordHash { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string UserType { get; set; } = "individual";
    public string? JobTitle { get; set; }
    public string? InterestField { get; set; }
    public Guid? CountryId { get; set; }
    public bool EmailVerified { get; set; }
    public bool PhoneVerified { get; set; }
    public UserStatus Status { get; set; } = UserStatus.PendingVerification;
    public string PreferredLanguage { get; set; } = "ar";

    /// True for Taqreerk team members (SuperAdmin / Admin / ContentReviewer).
    /// Used as a fast gate on /api/admin/* — set when seeded or when the
    /// SuperAdmin promotes a user via the staff-management UI (PR A2/Feature 1).
    public bool IsPlatformStaff { get; set; }

    // Brute-force protection: incremented on bad-password login; cleared on success.
    // When the count reaches the threshold, LockoutEndsAt is set forward; the next
    // login attempt before that timestamp is rejected without checking the password.
    public int FailedLoginAttempts { get; set; }
    public DateTime? LockoutEndsAt { get; set; }

    public Country? Country { get; set; }
    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
    public ICollection<UserRole> UserRoles { get; set; } = [];
    public ICollection<OrganizationMember> OrganizationMemberships { get; set; } = [];
    public ICollection<Report> UploadedReports { get; set; } = [];
    public ICollection<ReportRating> Ratings { get; set; } = [];
    public ICollection<ReportRecommendation> Recommendations { get; set; } = [];
    public ICollection<SavedReport> SavedReports { get; set; } = [];
    public ICollection<UserInterest> Interests { get; set; } = [];
    public ICollection<Notification> Notifications { get; set; } = [];
    public ICollection<ReportComparison> Comparisons { get; set; } = [];
    public ICollection<Infographic> CreatedInfographics { get; set; } = [];
    public ICollection<ReportView> ReportViews { get; set; } = [];
    public ICollection<Subscription> Subscriptions { get; set; } = [];
    public ICollection<AiJob> AiJobs { get; set; } = [];
    public ICollection<ChatSession> ChatSessions { get; set; } = [];
}
