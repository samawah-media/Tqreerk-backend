using Taqreerk.Domain.Common;
using Taqreerk.Domain.Enums;

namespace Taqreerk.Domain.Entities;

public class Payment : BaseEntity
{
    public Guid SubscriptionId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "SAR";
    public string? PaymentMethod { get; set; }
    public string? MiserPaymentReference { get; set; }
    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;
    public DateTime? PaidAt { get; set; }

    public Subscription Subscription { get; set; } = null!;
    public Invoice? Invoice { get; set; }
}
