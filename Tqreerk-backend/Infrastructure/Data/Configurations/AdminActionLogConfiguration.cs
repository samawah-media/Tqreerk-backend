using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class AdminActionLogConfiguration : IEntityTypeConfiguration<AdminActionLog>
{
    public void Configure(EntityTypeBuilder<AdminActionLog> builder)
    {
        builder.ToTable("admin_action_logs");

        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).HasDefaultValueSql("gen_random_uuid()");

        builder.Property(l => l.ActionType).IsRequired().HasMaxLength(100);
        builder.Property(l => l.TargetEntityType).IsRequired().HasMaxLength(50);
        builder.Property(l => l.IpAddress).HasMaxLength(45); // IPv6 max
        builder.Property(l => l.UserAgent).HasMaxLength(500);
        builder.Property(l => l.Reason).HasColumnType("text");
        builder.Property(l => l.BeforeState).HasColumnType("jsonb");
        builder.Property(l => l.AfterState).HasColumnType("jsonb");
        builder.Property(l => l.CreatedAt).HasDefaultValueSql("now()");

        // Two access patterns drive the indexes:
        //   - "what did THIS admin do recently?" → (AdminUserId, CreatedAt DESC)
        //   - "show the history of THIS report/org/user" → (TargetEntityType, TargetEntityId, CreatedAt DESC)
        // Both are descending on time so the most-recent rows fall on the
        // hot end of the index without an extra sort. The first index is
        // partial because system rows (AdminUserId = NULL) don't need to
        // appear in per-admin lookups.
        builder.HasIndex(l => new { l.AdminUserId, l.CreatedAt })
            .IsDescending(false, true)
            .HasFilter("\"AdminUserId\" IS NOT NULL")
            .HasDatabaseName("IX_admin_action_logs_AdminUserId_CreatedAt_desc");

        builder.HasIndex(l => new { l.TargetEntityType, l.TargetEntityId, l.CreatedAt })
            .IsDescending(false, false, true)
            .HasDatabaseName("IX_admin_action_logs_Target_CreatedAt_desc");

        // Restrict on delete so a staff member's row deletion doesn't
        // silently nuke their audit trail. Soft-deleting users (the only
        // path we expose) leaves the FK intact since we don't filter the
        // log table on the User's DeletedAt.
        builder.HasOne(l => l.AdminUser)
            .WithMany()
            .HasForeignKey(l => l.AdminUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
