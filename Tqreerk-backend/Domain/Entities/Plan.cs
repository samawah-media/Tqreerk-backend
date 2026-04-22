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
    public string AiAccessLevel { get; set; } = "basic";
    public bool ApiAccess { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<Subscription> Subscriptions { get; set; } = [];
}
