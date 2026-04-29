using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");

        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).HasDefaultValueSql("gen_random_uuid()");

        builder.Property(u => u.Email).IsRequired().HasMaxLength(255);
        builder.Property(u => u.Phone).HasMaxLength(20);
        builder.Property(u => u.PasswordHash).IsRequired().HasMaxLength(255);
        builder.Property(u => u.FullName).IsRequired().HasMaxLength(200);
        builder.Property(u => u.UserType).IsRequired().HasMaxLength(50).HasDefaultValue("individual");
        builder.Property(u => u.JobTitle).HasMaxLength(150);
        builder.Property(u => u.InterestField).HasMaxLength(150);
        builder.Property(u => u.PreferredLanguage).IsRequired().HasMaxLength(5).HasDefaultValue("ar");
        builder.Property(u => u.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(u => u.IsPlatformStaff).HasDefaultValue(false);
        builder.Property(u => u.CreatedAt).HasDefaultValueSql("now()");

        builder.HasIndex(u => u.Email).IsUnique();
        // Quick lookup for /api/admin/* gate. Partial index = nearly free; the
        // staff table is tiny vs the user table.
        builder.HasIndex(u => u.IsPlatformStaff)
            .HasFilter("\"IsPlatformStaff\" = TRUE");
        builder.HasIndex(u => u.Phone).IsUnique().HasFilter("\"Phone\" IS NOT NULL");

        builder.HasOne(u => u.Country)
            .WithMany(c => c.Users)
            .HasForeignKey(u => u.CountryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
