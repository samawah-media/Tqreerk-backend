using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class FeaturedReportConfiguration : IEntityTypeConfiguration<FeaturedReport>
{
    public void Configure(EntityTypeBuilder<FeaturedReport> builder)
    {
        builder.ToTable("featured_reports");

        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(f => f.CreatedAt).HasDefaultValueSql("now()");

        // Section is stored as int (the enum's underlying type). Cheap to
        // query, easy to extend — see FeaturedSection.cs for the rule on
        // appending new values.
        builder.Property(f => f.Section).HasConversion<int>();

        // (Section, Position) drives the kanban order. Not unique because
        // a transient gap during drag-drop would otherwise violate the
        // constraint mid-update. The admin service rebalances on save.
        builder.HasIndex(f => new { f.Section, f.Position });

        // Frequently used filter: "what's currently live in this section".
        builder.HasIndex(f => new { f.Section, f.IsActive });

        builder.HasOne(f => f.Report)
            .WithMany()
            .HasForeignKey(f => f.ReportId)
            .OnDelete(DeleteBehavior.Cascade);

        // Soft-link to the curator. SetNull on user delete so we don't
        // wipe the editorial decision when a staff member is removed.
        builder.HasOne(f => f.CreatedByUser)
            .WithMany()
            .HasForeignKey(f => f.CreatedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
