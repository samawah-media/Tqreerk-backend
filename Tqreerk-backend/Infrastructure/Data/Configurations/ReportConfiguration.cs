using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class ReportConfiguration : IEntityTypeConfiguration<Report>
{
    public void Configure(EntityTypeBuilder<Report> builder)
    {
        builder.ToTable("reports");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasDefaultValueSql("gen_random_uuid()");

        builder.Property(r => r.Title).IsRequired().HasMaxLength(500);
        builder.Property(r => r.Slug).IsRequired().HasMaxLength(500);
        builder.Property(r => r.OriginalLanguage).IsRequired().HasMaxLength(5).HasDefaultValue("ar");
        builder.Property(r => r.ReportType).HasMaxLength(100);
        builder.Property(r => r.FileUrl).HasMaxLength(1000);
        builder.Property(r => r.CoverImageUrl).HasMaxLength(1000);
        builder.Property(r => r.Source).HasMaxLength(255);
        builder.Property(r => r.Authors).HasMaxLength(2000);
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(r => r.SourceType).HasConversion<string>().HasMaxLength(30);
        builder.Property(r => r.AvgRating).HasPrecision(3, 2);
        builder.Property(r => r.CreatedAt).HasDefaultValueSql("now()");

        // tsvector column managed by DB trigger
        builder.Property(r => r.SearchVector)
            .HasColumnType("tsvector")
            .ValueGeneratedOnAddOrUpdate();

        builder.HasIndex(r => r.Slug).IsUnique();
        builder.HasIndex(r => r.SearchVector).HasMethod("GIN");
        builder.HasIndex(r => r.Status);
        builder.HasIndex(r => r.OrganizationId);
        // Used by the queue endpoint to find reports a specific reviewer
        // has currently claimed, and by the auto-release job to scan stale
        // claims by ClaimedAt — partial index keeps it tiny.
        builder.HasIndex(r => r.ClaimedByReviewerId)
            .HasFilter("\"ClaimedByReviewerId\" IS NOT NULL");

        builder.HasOne(r => r.Organization)
            .WithMany(o => o.Reports)
            .HasForeignKey(r => r.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.UploadedByUser)
            .WithMany(u => u.UploadedReports)
            .HasForeignKey(r => r.UploadedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // ClaimedByReviewer is optional and not tracked from the User side
        // (we don't need a `User.ClaimedReports` collection) — SetNull on
        // delete so a removed reviewer's stale claim doesn't block the
        // report from being moved.
        builder.HasOne(r => r.ClaimedByReviewer)
            .WithMany()
            .HasForeignKey(r => r.ClaimedByReviewerId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(r => r.Sector)
            .WithMany(s => s.Reports)
            .HasForeignKey(r => r.SectorId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(r => r.Country)
            .WithMany(c => c.Reports)
            .HasForeignKey(r => r.CountryId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
