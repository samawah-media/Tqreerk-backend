using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class OrganizationFileConfiguration : IEntityTypeConfiguration<OrganizationFile>
{
    public void Configure(EntityTypeBuilder<OrganizationFile> builder)
    {
        builder.ToTable("organization_files");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.FileType).IsRequired().HasMaxLength(100);
        builder.Property(e => e.FileUrl).IsRequired().HasMaxLength(500);

        builder.HasIndex(e => e.OrganizationId);
    }
}
