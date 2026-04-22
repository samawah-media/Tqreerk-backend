using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class SavedReportConfiguration : IEntityTypeConfiguration<SavedReport>
{
    public void Configure(EntityTypeBuilder<SavedReport> builder)
    {
        builder.ToTable("saved_reports");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");

        builder.HasIndex(e => new { e.UserId, e.ReportId }).IsUnique();
    }
}
