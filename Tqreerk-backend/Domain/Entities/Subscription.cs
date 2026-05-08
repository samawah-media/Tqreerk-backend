using Taqreerk.Domain.Common;
using Taqreerk.Domain.Enums;

namespace Taqreerk.Domain.Entities;

public class Subscription : BaseEntity
{
    public Guid? UserId { get; set; }
    public Guid? OrganizationId { get; set; }
    public Guid PlanId { get; set; }
    public string? MiserSubscriptionId { get; set; }
    public string? MiserCustomerId { get; set; }
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Inactive;
    public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Pending;
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public DateTime? GracePeriodEnd { get; set; }

    /// One-shot or annual extras layered on top of the base plan.
    /// Stored as a jsonb document so we don't need a separate addons
    /// table for v1 — see PLANS.md "Add-ons" for the catalogue and
    /// payload shape. Defaults to "{}" (empty object) so callers can
    /// always parse it without a null check.
    public string AddonsJson { get; set; } = "{}";

    public User? User { get; set; }
    public Organization? Organization { get; set; }
    public Plan Plan { get; set; } = null!;
    public ICollection<Payment> Payments { get; set; } = [];
    public ICollection<Invoice> Invoices { get; set; } = [];
}
