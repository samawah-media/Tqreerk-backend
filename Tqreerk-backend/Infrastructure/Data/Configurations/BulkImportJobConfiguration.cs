using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class BulkImportJobConfiguration : IEntityTypeConfiguration<BulkImportJob>
{
    public void Configure(EntityTypeBuilder<BulkImportJob> builder)
    {
        builder.ToTable("bulk_import_jobs");

        builder.HasKey(j => j.Id);
        builder.Property(j => j.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(j => j.CreatedAt).HasDefaultValueSql("now()");

        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(j => j.SourceFileName).HasMaxLength(500);
        builder.Property(j => j.ErrorMessage).HasMaxLength(2000);

        // Used by the admin "my recent imports" list — small index, very
        // selective query (creator + recency).
        builder.HasIndex(j => new { j.CreatedByUserId, j.CreatedAt });

        builder.HasOne(j => j.CreatedByUser)
            .WithMany()
            .HasForeignKey(j => j.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(j => j.Organization)
            .WithMany()
            .HasForeignKey(j => j.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(j => j.Items)
            .WithOne(i => i.Job)
            .HasForeignKey(i => i.JobId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
