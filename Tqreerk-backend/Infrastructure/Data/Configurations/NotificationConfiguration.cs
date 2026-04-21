using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");

        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(n => n.Type).IsRequired().HasMaxLength(100);
        builder.Property(n => n.TitleAr).IsRequired().HasMaxLength(300);
        builder.Property(n => n.TitleEn).IsRequired().HasMaxLength(300);
        builder.Property(n => n.Metadata).HasColumnType("jsonb");
        builder.Property(n => n.CreatedAt).HasDefaultValueSql("now()");

        builder.HasIndex(n => new { n.UserId, n.IsRead });

        builder.HasOne(n => n.User)
            .WithMany(u => u.Notifications)
            .HasForeignKey(n => n.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
