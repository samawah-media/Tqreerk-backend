using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class SectorConfiguration : IEntityTypeConfiguration<Sector>
{
    public void Configure(EntityTypeBuilder<Sector> builder)
    {
        builder.ToTable("sectors");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.NameAr).IsRequired().HasMaxLength(200);
        builder.Property(e => e.NameEn).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Slug).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Description).HasMaxLength(1000);

        builder.HasIndex(e => e.Slug).IsUnique();
    }
}
