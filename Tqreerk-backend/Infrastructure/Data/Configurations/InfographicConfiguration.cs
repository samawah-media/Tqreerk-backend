using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class InfographicConfiguration : IEntityTypeConfiguration<Infographic>
{
    public void Configure(EntityTypeBuilder<Infographic> builder)
    {
        builder.ToTable("infographics");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.ChartType).IsRequired().HasMaxLength(100);
        builder.Property(e => e.ChartData).HasColumnType("jsonb");
        builder.Property(e => e.ExportUrl).HasMaxLength(500);
    }
}
