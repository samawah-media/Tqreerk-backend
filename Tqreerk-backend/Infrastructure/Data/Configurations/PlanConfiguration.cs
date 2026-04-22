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
        builder.Property(e => e.AiAccessLevel).IsRequired().HasMaxLength(50);
    }
}
