using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("roles");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(r => r.Name).IsRequired().HasMaxLength(100);
        builder.Property(r => r.Description).HasMaxLength(500);
        builder.Property(r => r.Permissions).HasColumnType("jsonb");
        builder.Property(r => r.CreatedAt).HasDefaultValueSql("now()");

        builder.HasIndex(r => r.Name).IsUnique();

        // Seed default roles
        builder.HasData(
            new Role { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), Name = "admin", Description = "Organization administrator", CreatedAt = DateTime.UtcNow },
            new Role { Id = Guid.Parse("00000000-0000-0000-0000-000000000002"), Name = "editor", Description = "Can upload and edit reports", CreatedAt = DateTime.UtcNow },
            new Role { Id = Guid.Parse("00000000-0000-0000-0000-000000000003"), Name = "viewer", Description = "Read-only access", CreatedAt = DateTime.UtcNow }
        );
    }
}
