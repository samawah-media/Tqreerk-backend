using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class ReportTranslationConfiguration : IEntityTypeConfiguration<ReportTranslation>
{
    public void Configure(EntityTypeBuilder<ReportTranslation> builder)
    {
        builder.ToTable("report_translations");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(t => t.Language).IsRequired().HasMaxLength(5);
        builder.Property(t => t.TranslatedTitle).HasMaxLength(500);
        builder.Property(t => t.TranslatedFileUrl).HasMaxLength(1000);
        builder.Property(t => t.TranslationStatus).HasConversion<string>().HasMaxLength(30);
        builder.Property(t => t.CreatedAt).HasDefaultValueSql("now()");

        // Unique: one translation per language per report
        builder.HasIndex(t => new { t.ReportId, t.Language }).IsUnique();

        builder.HasOne(t => t.Report)
            .WithMany(r => r.Translations)
            .HasForeignKey(t => t.ReportId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(t => t.AiJob)
            .WithMany(j => j.Translations)
            .HasForeignKey(t => t.AiJobId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
