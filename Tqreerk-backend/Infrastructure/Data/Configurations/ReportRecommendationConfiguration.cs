using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class ReportRecommendationConfiguration : IEntityTypeConfiguration<ReportRecommendation>
{
    public void Configure(EntityTypeBuilder<ReportRecommendation> builder)
    {
        builder.ToTable("report_recommendations");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.ShareChannel).HasMaxLength(100);
    }
}
