using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class ReportAnnotationConfiguration : IEntityTypeConfiguration<ReportAnnotation>
{
    public void Configure(EntityTypeBuilder<ReportAnnotation> builder)
    {
        builder.ToTable("report_annotations");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(a => a.CreatedAt).HasDefaultValueSql("now()");

        // SelectionText is no longer required — drag-paint highlights
        // don't capture text. Kept nullable-ish via a column default
        // so legacy rows from 1.2a/b/c still validate.
        builder.Property(a => a.SelectionText).HasMaxLength(2000).HasDefaultValue("");
        builder.Property(a => a.SelectionRect).HasColumnType("jsonb").IsRequired();
        builder.Property(a => a.Color).IsRequired().HasMaxLength(20);
        // Notes can hold a few paragraphs — sticky-note bodies, not
        // tweet-length tags. 4000 covers a screen of dense Arabic text.
        builder.Property(a => a.Note).HasMaxLength(4000);

        // Persisted as the enum's string name so the column reads
        // naturally in DB tooling and reorders are safe.
        builder.Property(a => a.Type).HasConversion<string>().HasMaxLength(20);

        // Listing query: WHERE user_id = @u AND report_id = @r
        // The (UserId, ReportId) index covers it without a sort.
        builder.HasIndex(a => new { a.UserId, a.ReportId })
            .HasDatabaseName("ix_report_annotations_user_report");

        builder.HasOne(a => a.User)
            .WithMany()
            .HasForeignKey(a => a.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(a => a.Report)
            .WithMany()
            .HasForeignKey(a => a.ReportId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
