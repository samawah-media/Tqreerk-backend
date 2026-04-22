using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class ReportViewConfiguration : IEntityTypeConfiguration<ReportView>
{
    public void Configure(EntityTypeBuilder<ReportView> builder)
    {
        builder.ToTable("report_views");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.IpAddress).HasMaxLength(45);
        builder.Property(e => e.UserAgent).HasMaxLength(500);

        builder.HasIndex(e => e.ReportId);
        builder.HasIndex(e => e.ViewedAt);
    }
}
