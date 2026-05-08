using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> builder)
    {
        builder.ToTable("subscriptions");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(s => s.PaymentStatus).HasConversion<string>().HasMaxLength(30);
        builder.Property(s => s.MiserSubscriptionId).HasMaxLength(255);
        builder.Property(s => s.MiserCustomerId).HasMaxLength(255);
        builder.Property(s => s.CreatedAt).HasDefaultValueSql("now()");

        // jsonb so we can query into the addons via Postgres operators
        // later (e.g. find every sub with an active promote_homepage
        // addon). Default '{}' on the DB side too so existing rows
        // pre-migration always parse cleanly.
        builder.Property(s => s.AddonsJson)
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb");

        builder.HasOne(s => s.User)
            .WithMany(u => u.Subscriptions)
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(s => s.Organization)
            .WithMany(o => o.Subscriptions)
            .HasForeignKey(s => s.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(s => s.Plan)
            .WithMany(p => p.Subscriptions)
            .HasForeignKey(s => s.PlanId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
