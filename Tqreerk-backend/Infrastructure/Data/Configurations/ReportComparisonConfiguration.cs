using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class ReportComparisonConfiguration : IEntityTypeConfiguration<ReportComparison>
{
    public void Configure(EntityTypeBuilder<ReportComparison> builder)
    {
        builder.ToTable("report_comparisons");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(c => c.ReportIds).IsRequired().HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
        builder.Property(c => c.SimilarityScore).HasPrecision(5, 4);
        builder.Property(c => c.CreatedAt).HasDefaultValueSql("now()");

        builder.HasOne(c => c.User)
            .WithMany(u => u.Comparisons)
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(c => c.AiJob)
            .WithMany(j => j.Comparisons)
            .HasForeignKey(c => c.AiJobId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
