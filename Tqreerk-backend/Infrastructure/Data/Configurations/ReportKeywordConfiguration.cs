using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class ReportKeywordConfiguration : IEntityTypeConfiguration<ReportKeyword>
{
    public void Configure(EntityTypeBuilder<ReportKeyword> builder)
    {
        builder.ToTable("report_keywords");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.Keyword).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Language).IsRequired().HasMaxLength(10);

        builder.HasIndex(e => new { e.ReportId, e.Keyword, e.Language }).IsUnique();
    }
}
