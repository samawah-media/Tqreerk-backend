using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class UsageTrackingConfiguration : IEntityTypeConfiguration<UsageTracking>
{
    public void Configure(EntityTypeBuilder<UsageTracking> builder)
    {
        builder.ToTable("usage_tracking");

        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).HasDefaultValueSql("gen_random_uuid()");

        // String-stored enum — see UsageActionType doc comment.
        builder.Property(u => u.ActionType).HasConversion<string>().HasMaxLength(40);

        builder.Property(u => u.Metadata).HasColumnType("jsonb");
        builder.Property(u => u.ConsumedAt).HasDefaultValueSql("now()");
        builder.Property(u => u.CreatedAt).HasDefaultValueSql("now()");

        // Cap-check query lives entirely on this index:
        //   WHERE user_id = @u AND action_type = @a AND billing_period_start = @m
        builder.HasIndex(u => new { u.UserId, u.ActionType, u.BillingPeriodStart })
            .HasDatabaseName("ix_usage_tracking_user_action_period");

        // For per-user history listing.
        builder.HasIndex(u => new { u.UserId, u.ConsumedAt })
            .HasDatabaseName("ix_usage_tracking_user_consumed");

        builder.HasOne(u => u.User)
            .WithMany()
            .HasForeignKey(u => u.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(u => u.Organization)
            .WithMany()
            .HasForeignKey(u => u.OrganizationId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(u => u.Subscription)
            .WithMany()
            .HasForeignKey(u => u.SubscriptionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
