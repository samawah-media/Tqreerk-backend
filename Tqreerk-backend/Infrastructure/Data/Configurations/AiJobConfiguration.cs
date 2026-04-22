using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class AiJobConfiguration : IEntityTypeConfiguration<AiJob>
{
    public void Configure(EntityTypeBuilder<AiJob> builder)
    {
        builder.ToTable("ai_jobs");

        builder.HasKey(j => j.Id);
        builder.Property(j => j.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(j => j.JobType).HasConversion<string>().HasMaxLength(50);
        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(j => j.InputData).HasColumnType("jsonb");
        builder.Property(j => j.OutputData).HasColumnType("jsonb");
        builder.Property(j => j.CreatedAt).HasDefaultValueSql("now()");

        builder.HasOne(j => j.Organization)
            .WithMany(o => o.AiJobs)
            .HasForeignKey(j => j.OrganizationId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(j => j.User)
            .WithMany(u => u.AiJobs)
            .HasForeignKey(j => j.UserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(j => j.Report)
            .WithMany(r => r.AiJobs)
            .HasForeignKey(j => j.ReportId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
