using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class ReportCommentConfiguration : IEntityTypeConfiguration<ReportComment>
{
    public void Configure(EntityTypeBuilder<ReportComment> builder)
    {
        builder.ToTable("report_comments");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(c => c.CreatedAt).HasDefaultValueSql("now()");

        builder.Property(c => c.Body).IsRequired().HasMaxLength(4000);

        // Listing comments by report is the dominant query. Pair with
        // CreatedAt so the index covers ORDER BY too.
        builder.HasIndex(c => new { c.ReportId, c.CreatedAt });
        builder.HasIndex(c => c.UserId);

        builder.HasOne(c => c.Report)
            .WithMany()
            .HasForeignKey(c => c.ReportId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(c => c.User)
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
