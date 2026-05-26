using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class OrganizationInvitationConfiguration : IEntityTypeConfiguration<OrganizationInvitation>
{
    public void Configure(EntityTypeBuilder<OrganizationInvitation> builder)
    {
        builder.ToTable("organization_invitations");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(i => i.CreatedAt).HasDefaultValueSql("now()");

        builder.Property(i => i.Email).IsRequired().HasMaxLength(255);
        builder.Property(i => i.TokenHash).IsRequired().HasMaxLength(128);
        builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(30);

        builder.HasIndex(i => i.TokenHash).IsUnique();
        // Quick lookups for the dashboard "pending invitations" view + duplicate guard.
        builder.HasIndex(i => new { i.OrganizationId, i.Email, i.Status });

        builder.HasOne(i => i.Organization)
            .WithMany()
            .HasForeignKey(i => i.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(i => i.InvitedByUser)
            .WithMany()
            .HasForeignKey(i => i.InvitedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
