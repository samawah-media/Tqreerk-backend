using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class OrganizationProfileConfiguration : IEntityTypeConfiguration<OrganizationProfile>
{
    public void Configure(EntityTypeBuilder<OrganizationProfile> builder)
    {
        builder.ToTable("organization_profiles");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.CommercialRegisterNo).HasMaxLength(100);
        builder.Property(e => e.CommercialRegisterName).HasMaxLength(300);
        builder.Property(e => e.TaxNumber).HasMaxLength(100);
        builder.Property(e => e.LicenseDocumentUrl).HasMaxLength(500);
        builder.Property(e => e.ContactPersonName).HasMaxLength(200);
        builder.Property(e => e.ContactPersonTitle).HasMaxLength(200);
        builder.Property(e => e.ContactEmail).HasMaxLength(200);
    }
}
