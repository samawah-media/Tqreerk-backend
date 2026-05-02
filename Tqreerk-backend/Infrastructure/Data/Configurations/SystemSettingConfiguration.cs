using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;
using Taqreerk.Infrastructure.Data.Seed;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class SystemSettingConfiguration : IEntityTypeConfiguration<SystemSetting>
{
    public void Configure(EntityTypeBuilder<SystemSetting> builder)
    {
        builder.ToTable("system_settings");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(s => s.CreatedAt).HasDefaultValueSql("now()");

        builder.Property(s => s.Key).IsRequired().HasMaxLength(150);
        builder.Property(s => s.Value).IsRequired().HasMaxLength(4000);
        builder.Property(s => s.Category).IsRequired().HasMaxLength(50);
        builder.Property(s => s.ValueType).IsRequired().HasMaxLength(20);
        builder.Property(s => s.Description).HasMaxLength(500);

        // Lookup is always by Key — uniqueness enforced at the index.
        builder.HasIndex(s => s.Key).IsUnique();
        builder.HasIndex(s => s.Category);

        builder.HasData(SystemSettingsSeedData.DefaultSettings);
    }
}
