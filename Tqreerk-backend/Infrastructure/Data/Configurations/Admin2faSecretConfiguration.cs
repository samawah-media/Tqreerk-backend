using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class Admin2faSecretConfiguration : IEntityTypeConfiguration<Admin2faSecret>
{
    public void Configure(EntityTypeBuilder<Admin2faSecret> builder)
    {
        builder.ToTable("admin_2fa_secrets");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasDefaultValueSql("gen_random_uuid()");

        // Both encrypted blobs are bounded so we don't accidentally allow
        // a runaway payload. EncryptedBackupCodes carries up to ~10 codes
        // so even with the encryption envelope it stays well under 4 KB.
        builder.Property(s => s.EncryptedSecret).IsRequired().HasMaxLength(2000);
        builder.Property(s => s.EncryptedBackupCodes).IsRequired().HasMaxLength(8000);
        builder.Property(s => s.CreatedAt).HasDefaultValueSql("now()");

        // Unique on UserId — at most one 2FA row per user. Cascade so
        // soft-deleting the user (which we don't do today, but might) or
        // a hard cleanup tears the secret down with it.
        builder.HasIndex(s => s.UserId).IsUnique();

        builder.HasOne(s => s.User)
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
