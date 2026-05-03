using Taqreerk.Domain.Common;
using Taqreerk.Domain.Enums;

namespace Taqreerk.Domain.Entities;

public class Plan : BaseEntity
{
    public string NameAr { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public PlanTargetType TargetType { get; set; }
    public decimal AnnualPrice { get; set; }
    public string? MiserPriceId { get; set; }
    public int UserLimit { get; set; }
    public int ReportsDownloadLimit { get; set; }
    public int AiCallsLimit { get; set; }
    public int FeaturedReportsMonthly { get; set; }

    // -1 = unlimited, 0 = blocked. Apply to individual plans only; org
    // plans use their own user/report counters.
    public int IndividualReadsLimit { get; set; }
    public int IndividualSavedReportsLimit { get; set; }
    public string AiAccessLevel { get; set; } = "basic";
    public bool ApiAccess { get; set; }
    public bool IsActive { get; set; } = true;

    // Per-month caps for individual users — read by UsageService when
    // the [EnforceUsageLimit] attribute checks the current counter
    // against the user's plan. Org plans use ReportsDownloadLimit /
    // AiCallsLimit instead, so these stay 0 there.
    public int IndividualReadsLimit { get; set; }
    public int IndividualSavedReportsLimit { get; set; }

    public ICollection<Subscription> Subscriptions { get; set; } = [];
}
