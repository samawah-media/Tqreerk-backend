using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class ReportPageConfiguration : IEntityTypeConfiguration<ReportPage>
{
    public void Configure(EntityTypeBuilder<ReportPage> builder)
    {
        builder.ToTable("report_pages");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(p => p.Content).IsRequired();
        builder.Property(p => p.CreatedAt).HasDefaultValueSql("now()");

        // vector(768) embedding column is added by the migration via raw SQL and managed by
        // the Python ai-service only — EF Core never maps it.
        // No global ANN index: WHERE ReportId = ? limits each search to ~100 rows,
        // so a sequential cosine scan is faster than HNSW at any report count.

        builder.HasIndex(p => new { p.ReportId, p.PageNumber }).IsUnique();

        builder.HasOne(p => p.Report)
            .WithMany(r => r.Pages)
            .HasForeignKey(p => p.ReportId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
