using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class PartnerConfiguration : IEntityTypeConfiguration<Partner>
{
    public void Configure(EntityTypeBuilder<Partner> builder)
    {
        builder.ToTable("Partners");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.NameAr).IsRequired().HasMaxLength(200);
        builder.Property(e => e.NameEn).IsRequired().HasMaxLength(200);
        builder.Property(e => e.LogoUrl).HasMaxLength(1000);
        builder.Property(e => e.WebsiteUrl).HasMaxLength(2000);
        builder.Property(e => e.IsActive).HasDefaultValue(true);
        builder.Property(e => e.SortOrder).HasDefaultValue(0);

        builder.HasOne(e => e.Category)
            .WithMany(c => c.Partners)
            .HasForeignKey(e => e.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.CategoryId, e.IsActive, e.SortOrder });
        builder.HasIndex(e => new { e.IsActive, e.SortOrder });
    }
}
