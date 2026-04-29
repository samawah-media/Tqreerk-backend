using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class ReportReviewConfiguration : IEntityTypeConfiguration<ReportReview>
{
    public void Configure(EntityTypeBuilder<ReportReview> builder)
    {
        builder.ToTable("report_reviews");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasDefaultValueSql("gen_random_uuid()");

        builder.Property(r => r.Decision)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(r => r.ReviewNotes).HasColumnType("text");
        builder.Property(r => r.InternalNotes).HasColumnType("text");
        builder.Property(r => r.CreatedAt).HasDefaultValueSql("now()");

        // Latest-review-first lookup. The history tab on the workspace page
        // and the org-side "what did the reviewer say last time" both want
        // the most recent review for a report — index supports both.
        builder.HasIndex(r => new { r.ReportId, r.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("IX_report_reviews_ReportId_CreatedAt_desc");

        // Reviewer-centric lookup for /admin/reviews/my-history (PR A4).
        builder.HasIndex(r => r.ReviewerUserId);

        builder.HasOne(r => r.Report)
            .WithMany()
            .HasForeignKey(r => r.ReportId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.Reviewer)
            .WithMany()
            .HasForeignKey(r => r.ReviewerUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
