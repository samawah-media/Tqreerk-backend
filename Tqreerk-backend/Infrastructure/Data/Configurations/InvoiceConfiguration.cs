using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Configurations;

public class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("invoices");

        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).HasDefaultValueSql("gen_random_uuid()");
        builder.Property(i => i.InvoiceNumber).IsRequired().HasMaxLength(50);
        builder.Property(i => i.Amount).HasPrecision(12, 2);
        builder.Property(i => i.PdfUrl).HasMaxLength(1000);
        builder.Property(i => i.IssuedAt).HasDefaultValueSql("now()");
        builder.Property(i => i.CreatedAt).HasDefaultValueSql("now()");

        builder.HasIndex(i => i.InvoiceNumber).IsUnique();

        builder.HasOne(i => i.Subscription)
            .WithMany(s => s.Invoices)
            .HasForeignKey(i => i.SubscriptionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(i => i.Payment)
            .WithOne(p => p.Invoice)
            .HasForeignKey<Invoice>(i => i.PaymentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
