using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class ReportFeatureRequestConfiguration : IEntityTypeConfiguration<ReportFeatureRequest>
{
    public void Configure(EntityTypeBuilder<ReportFeatureRequest> builder)
    {
        builder.ToTable("report_feature_requests");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(r => r.CreatedAt).HasDefaultValueSql("now()");

        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.Note).HasMaxLength(1000);
        builder.Property(r => r.DecisionNote).HasMaxLength(1000);

        // Admin queue listing query (status filter, newest first):
        //   WHERE status = 'Pending' ORDER BY created_at DESC
        // Composite covers both predicates without an extra sort step.
        builder.HasIndex(r => new { r.Status, r.CreatedAt })
            .HasDatabaseName("ix_report_feature_requests_status_created");

        // Per-org listing query (org dashboard "my feature requests"):
        builder.HasIndex(r => new { r.OrganizationId, r.CreatedAt })
            .HasDatabaseName("ix_report_feature_requests_org_created");

        // Enforce "at most one Pending request per report" — partial
        // unique index on Postgres so Approved/Rejected rows don't
        // block the org from re-submitting after a rejection.
        builder.HasIndex(r => r.ReportId)
            .IsUnique()
            .HasFilter("\"Status\" = 'Pending'")
            .HasDatabaseName("ux_report_feature_requests_pending_report");

        builder.HasOne(r => r.Report)
            .WithMany()
            .HasForeignKey(r => r.ReportId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.Organization)
            .WithMany()
            .HasForeignKey(r => r.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.RequestedByUser)
            .WithMany()
            .HasForeignKey(r => r.RequestedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.ReviewedByUser)
            .WithMany()
            .HasForeignKey(r => r.ReviewedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
