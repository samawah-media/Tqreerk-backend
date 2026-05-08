using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> builder)
    {
        builder.ToTable("plans");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.NameAr).IsRequired().HasMaxLength(200);
        builder.Property(e => e.NameEn).IsRequired().HasMaxLength(200);
        builder.Property(e => e.AnnualPrice).HasPrecision(12, 2);
        builder.Property(e => e.MiserPriceId).HasMaxLength(100);

        // Tier labels — short string enums. Stored as plain text so DB
        // tooling reads naturally. The application validates the value
        // set; nothing on the DB side enforces the enum, by design.
        builder.Property(e => e.AiAccessLevel).IsRequired().HasMaxLength(50);
        builder.Property(e => e.AdvancedSearchPrecision).IsRequired().HasMaxLength(20);
        builder.Property(e => e.OrgPageTier).IsRequired().HasMaxLength(20);
        builder.Property(e => e.SupportTier).IsRequired().HasMaxLength(20);
        builder.Property(e => e.DashboardTier).IsRequired().HasMaxLength(20);
        builder.Property(e => e.NotificationsTier).IsRequired().HasMaxLength(20);
        builder.Property(e => e.UpdatesCadence).IsRequired().HasMaxLength(20);
    }
}
