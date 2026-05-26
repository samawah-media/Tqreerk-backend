using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;
using Taqreerk.Infrastructure.Data.Seed;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class PageConfiguration : IEntityTypeConfiguration<Page>
{
    public void Configure(EntityTypeBuilder<Page> builder)
    {
        builder.ToTable("pages");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(p => p.Key).IsRequired().HasMaxLength(100);
        builder.Property(p => p.NameEn).IsRequired().HasMaxLength(200);
        builder.Property(p => p.NameAr).IsRequired().HasMaxLength(200);
        builder.Property(p => p.Description).HasMaxLength(1000);
        builder.Property(p => p.SortOrder).HasDefaultValue(0);
        builder.Property(p => p.IsSystem).HasDefaultValue(false);
        builder.Property(p => p.CreatedAt).HasDefaultValueSql("now()");

        builder.HasIndex(p => p.Key).IsUnique();

        builder.HasData(RbacSeedData.Pages);
    }
}
