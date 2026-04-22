using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class OrganizationMemberConfiguration : IEntityTypeConfiguration<OrganizationMember>
{
    public void Configure(EntityTypeBuilder<OrganizationMember> builder)
    {
        builder.ToTable("organization_members");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(m => m.JoinedAt).HasDefaultValueSql("now()");
        builder.Property(m => m.IsActive).HasDefaultValue(true);

        builder.HasIndex(m => new { m.OrganizationId, m.UserId }).IsUnique();

        builder.HasOne(m => m.Organization)
            .WithMany(o => o.Members)
            .HasForeignKey(m => m.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.User)
            .WithMany(u => u.OrganizationMemberships)
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.Role)
            .WithMany(r => r.OrganizationMembers)
            .HasForeignKey(m => m.RoleId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
