using Microsoft.EntityFrameworkCore;
using Taqreerk.Domain.Common;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data;

public class TaqreerkDbContext : DbContext
{
    public TaqreerkDbContext(DbContextOptions<TaqreerkDbContext> options) : base(options) { }

    // Identity & Auth
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    // Organizations
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationProfile> OrganizationProfiles => Set<OrganizationProfile>();
    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();
    public DbSet<OrganizationFile> OrganizationFiles => Set<OrganizationFile>();
    public DbSet<OrganizationInvitation> OrganizationInvitations => Set<OrganizationInvitation>();

    // Reference data
    public DbSet<Country> Countries => Set<Country>();
    public DbSet<Sector> Sectors => Set<Sector>();

    // Reports
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<ReportReview> ReportReviews => Set<ReportReview>();
    public DbSet<ReportTranslation> ReportTranslations => Set<ReportTranslation>();
    public DbSet<ReportKeyword> ReportKeywords => Set<ReportKeyword>();
    public DbSet<ReportAiContent> ReportAiContents => Set<ReportAiContent>();
    public DbSet<ReportComparison> ReportComparisons => Set<ReportComparison>();
    public DbSet<ReportRating> ReportRatings => Set<ReportRating>();
    public DbSet<ReportRecommendation> ReportRecommendations => Set<ReportRecommendation>();
    public DbSet<ReportView> ReportViews => Set<ReportView>();
    public DbSet<SavedReport> SavedReports => Set<SavedReport>();
    public DbSet<Infographic> Infographics => Set<Infographic>();

    // AI
    public DbSet<AiJob> AiJobs => Set<AiJob>();
    public DbSet<ReportChunk> ReportChunks => Set<ReportChunk>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    // User interaction
    public DbSet<UserInterest> UserInterests => Set<UserInterest>();
    public DbSet<Notification> Notifications => Set<Notification>();

    // Billing
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Invoice> Invoices => Set<Invoice>();

    // Audit
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<AdminActionLog> AdminActionLogs => Set<AdminActionLog>();

    // Admin 2FA
    public DbSet<Admin2faSecret> Admin2faSecrets => Set<Admin2faSecret>();

    // RBAC
    public DbSet<Page> Pages => Set<Page>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();

    // Account flows
    public DbSet<EmailVerificationToken> EmailVerificationTokens => Set<EmailVerificationToken>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TaqreerkDbContext).Assembly);

        // Global soft-delete filters
        modelBuilder.Entity<User>().HasQueryFilter(e => e.DeletedAt == null);
        modelBuilder.Entity<Organization>().HasQueryFilter(e => e.DeletedAt == null);
        modelBuilder.Entity<Report>().HasQueryFilter(e => e.DeletedAt == null);
        modelBuilder.Entity<ReportTranslation>().HasQueryFilter(e => e.DeletedAt == null);
        modelBuilder.Entity<ReportAiContent>().HasQueryFilter(e => e.DeletedAt == null);
        modelBuilder.Entity<Infographic>().HasQueryFilter(e => e.DeletedAt == null);
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            if (entry.State == EntityState.Added)
                entry.Entity.CreatedAt = now;
        }

        foreach (var entry in ChangeTracker.Entries<AuditableEntity>())
        {
            if (entry.State == EntityState.Modified)
                entry.Entity.UpdatedAt = now;
        }

        return await base.SaveChangesAsync(cancellationToken);
    }
}
