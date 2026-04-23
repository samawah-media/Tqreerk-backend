using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;
using Taqreerk.Infrastructure.Data.Seed;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("permissions");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(p => p.Key).IsRequired().HasMaxLength(100);
        builder.Property(p => p.NameEn).IsRequired().HasMaxLength(200);
        builder.Property(p => p.NameAr).IsRequired().HasMaxLength(200);
        builder.Property(p => p.Description).HasMaxLength(1000);
        builder.Property(p => p.IsSystem).HasDefaultValue(false);
        builder.Property(p => p.CreatedAt).HasDefaultValueSql("now()");

        builder.HasIndex(p => new { p.PageId, p.Key }).IsUnique();

        builder.HasOne(p => p.Page)
               .WithMany(page => page.Permissions)
               .HasForeignKey(p => p.PageId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasData(RbacSeedData.Permissions);
    }
}
