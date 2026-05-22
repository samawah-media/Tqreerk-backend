using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class BulkImportItemConfiguration : IEntityTypeConfiguration<BulkImportItem>
{
    public void Configure(EntityTypeBuilder<BulkImportItem> builder)
    {
        builder.ToTable("bulk_import_items");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(i => i.CreatedAt).HasDefaultValueSql("now()");

        builder.Property(i => i.Stage).HasConversion<string>().HasMaxLength(30);
        builder.Property(i => i.Title).IsRequired().HasMaxLength(500);
        builder.Property(i => i.TitleEn).IsRequired().HasMaxLength(500);
        builder.Property(i => i.FileUrl).IsRequired().HasMaxLength(2000);
        builder.Property(i => i.ReportType).HasMaxLength(100);
        builder.Property(i => i.Source).HasMaxLength(255);
        builder.Property(i => i.Authors).HasMaxLength(2000);
        builder.Property(i => i.OriginalLanguage).HasMaxLength(5);
        builder.Property(i => i.SectorNameAr).HasMaxLength(255);
        builder.Property(i => i.CountryNameAr).HasMaxLength(255);
        builder.Property(i => i.ErrorMessage).HasMaxLength(4000);

        // The processor query reads "items in this job, ordered by row index"
        // for the progress UI; this index is the fast path.
        builder.HasIndex(i => new { i.JobId, i.RowIndex });

        // Findall-by-stage scans (e.g. the processor picking up Pending /
        // poll-checking Ingesting/Summarizing items) want a status index.
        builder.HasIndex(i => i.Stage);

        // SetNull on Report deletion so nuking a report doesn't take its
        // import-history breadcrumb with it.
        builder.HasOne(i => i.Report)
            .WithMany()
            .HasForeignKey(i => i.ReportId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
