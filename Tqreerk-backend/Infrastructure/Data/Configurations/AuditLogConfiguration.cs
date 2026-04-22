using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(a => a.EventType).IsRequired().HasMaxLength(100);
        builder.Property(a => a.EntityType).IsRequired().HasMaxLength(100);
        builder.Property(a => a.IpAddress).HasMaxLength(50);
        builder.Property(a => a.Payload).HasColumnType("jsonb");
        builder.Property(a => a.CreatedAt).HasDefaultValueSql("now()");

        builder.HasIndex(a => a.EntityType);
        builder.HasIndex(a => a.EntityId);

        builder.HasOne(a => a.Organization)
            .WithMany(o => o.AuditLogs)
            .HasForeignKey(a => a.OrganizationId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
