using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(p => p.Amount).HasPrecision(12, 2);
        builder.Property(p => p.Currency).IsRequired().HasMaxLength(5).HasDefaultValue("SAR");
        builder.Property(p => p.PaymentMethod).HasMaxLength(100);
        builder.Property(p => p.MiserPaymentReference).HasMaxLength(255);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(p => p.CreatedAt).HasDefaultValueSql("now()");

        builder.HasOne(p => p.Subscription)
            .WithMany(s => s.Payments)
            .HasForeignKey(p => p.SubscriptionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
