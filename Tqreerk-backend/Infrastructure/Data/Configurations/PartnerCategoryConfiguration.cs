using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class PartnerCategoryConfiguration : IEntityTypeConfiguration<PartnerCategory>
{
    public static readonly Guid DefaultCategoryId =
        Guid.Parse("a0000000-0000-0000-0000-000000000001");

    public void Configure(EntityTypeBuilder<PartnerCategory> builder)
    {
        builder.ToTable("PartnerCategories");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.NameAr).IsRequired().HasMaxLength(200);
        builder.Property(e => e.NameEn).IsRequired().HasMaxLength(200);
        builder.Property(e => e.IsActive).HasDefaultValue(true);
        builder.Property(e => e.SortOrder).HasDefaultValue(0);

        builder.HasIndex(e => new { e.IsActive, e.SortOrder });
    }
}
