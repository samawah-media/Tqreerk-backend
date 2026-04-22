using Taqreerk.Domain.Common;

namespace Taqreerk.Domain.Entities;

public class Invoice : BaseEntity
{
    public Guid SubscriptionId { get; set; }
    public Guid PaymentId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string? PdfUrl { get; set; }
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;

    public Subscription Subscription { get; set; } = null!;
    public Payment Payment { get; set; } = null!;
}
