using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("organizations");

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).HasDefaultValueSql("gen_random_uuid()");

        builder.Property(o => o.NameAr).IsRequired().HasMaxLength(300);
        builder.Property(o => o.NameEn).IsRequired().HasMaxLength(300);
        builder.Property(o => o.Slug).IsRequired().HasMaxLength(300);
        builder.Property(o => o.Type).HasConversion<string>().HasMaxLength(30);
        builder.Property(o => o.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(o => o.SectorScope).HasMaxLength(100);
        builder.Property(o => o.City).HasMaxLength(100);
        builder.Property(o => o.Phone).HasMaxLength(20);
        builder.Property(o => o.WebsiteUrl).HasMaxLength(500);
        builder.Property(o => o.LogoUrl).HasMaxLength(1000);
        builder.Property(o => o.CreatedAt).HasDefaultValueSql("now()");

        // Partial: same reasoning as users.email — soft-deleted orgs keep
        // their slug, so uniqueness only applies among active orgs.
        builder.HasIndex(o => o.Slug).IsUnique().HasFilter("\"DeletedAt\" IS NULL");

        builder.HasOne(o => o.Country)
            .WithMany(c => c.Organizations)
            .HasForeignKey(o => o.CountryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(o => o.Profile)
            .WithOne(p => p.Organization)
            .HasForeignKey<OrganizationProfile>(p => p.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
