using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class ReportAiContentConfiguration : IEntityTypeConfiguration<ReportAiContent>
{
    public void Configure(EntityTypeBuilder<ReportAiContent> builder)
    {
        builder.ToTable("report_ai_contents");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(c => c.Language).IsRequired().HasMaxLength(5);
        builder.Property(c => c.Summary).HasColumnType("jsonb");
        builder.Property(c => c.KeyFindings).HasColumnType("jsonb");
        builder.Property(c => c.Topics).HasColumnType("jsonb");
        builder.Property(c => c.Recommendations).HasColumnType("jsonb");
        builder.Property(c => c.Indicators).HasColumnType("jsonb");
        builder.Property(c => c.Trends).HasColumnType("jsonb");
        builder.Property(c => c.CreatedAt).HasDefaultValueSql("now()");

        builder.HasIndex(c => new { c.ReportId, c.Language }).IsUnique();

        builder.HasOne(c => c.Report)
            .WithMany(r => r.AiContents)
            .HasForeignKey(c => c.ReportId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(c => c.AiJob)
            .WithMany(j => j.AiContents)
            .HasForeignKey(c => c.AiJobId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
