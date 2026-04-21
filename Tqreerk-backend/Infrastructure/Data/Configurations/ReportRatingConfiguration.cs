using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class ReportRatingConfiguration : IEntityTypeConfiguration<ReportRating>
{
    public void Configure(EntityTypeBuilder<ReportRating> builder)
    {
        builder.ToTable("report_ratings");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");

        builder.Property(e => e.Review).HasMaxLength(2000);

        builder.HasIndex(e => new { e.ReportId, e.UserId }).IsUnique();
    }
}
