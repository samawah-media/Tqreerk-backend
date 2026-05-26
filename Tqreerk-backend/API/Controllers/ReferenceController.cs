using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.DTOs.Organizations;
using Taqreerk.Application.DTOs.Plans;
using Taqreerk.Application.Interfaces;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.API.Controllers;

/// Public reference data (countries, sectors, plans) used by signup/wizard dropdowns
/// and the pricing page. No auth required — these are static lookup tables.
[ApiController]
[Route("api")]
[Produces("application/json")]
[AllowAnonymous]
public class ReferenceController : ControllerBase
{
    private readonly IOrganizationService _orgs;
    private readonly TaqreerkDbContext    _db;

    public ReferenceController(IOrganizationService orgs, TaqreerkDbContext db)
    {
        _orgs = orgs;
        _db   = db;
    }

    [HttpGet("countries")]
    [ResponseCache(Duration = 600)]
    [OutputCache(PolicyName = "Reference")]
    [ProducesResponseType(typeof(IReadOnlyList<CountryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCountries(CancellationToken ct)
        => Ok(await _orgs.ListCountriesAsync(ct));

    [HttpGet("sectors")]
    [ResponseCache(Duration = 600)]
    [OutputCache(PolicyName = "Reference")]
    [ProducesResponseType(typeof(IReadOnlyList<SectorDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSectors(CancellationToken ct)
        => Ok(await _orgs.ListSectorsAsync(ct));

    /// <summary>All active plans for the public pricing page.
    /// Returns a slim DTO with a pre-computed Arabic feature list so the
    /// SPA doesn't need to interpret raw limit numbers.</summary>
    [HttpGet("plans")]
    [ResponseCache(Duration = 300)]
    [ProducesResponseType(typeof(IReadOnlyList<PublicPlanDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPlans(CancellationToken ct)
    {
        var plans = await _db.Plans
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.TargetType)
            .ThenBy(p => p.AnnualPrice)
            .ToListAsync(ct);

        // Within each target-type group the most expensive active plan
        // gets the "highlighted" flag so it appears as the recommended tier.
        var result = plans
            .GroupBy(p => p.TargetType)
            .SelectMany(g =>
            {
                var ordered   = g.OrderByDescending(p => p.AnnualPrice).ToList();
                var topPlanId = ordered.First().Id;
                return g.Select(p => new PublicPlanDto(
                    p.Id,
                    p.NameAr,
                    p.NameEn,
                    p.TargetType.ToString(),
                    p.AnnualPrice,
                    IsHighlighted: p.Id == topPlanId,
                    FeaturesAr: BuildFeaturesAr(p),
                    FeaturesEn: BuildFeaturesEn(p)));
            })
            .OrderBy(p => p.TargetType)
            .ThenBy(p => p.AnnualPrice)
            .ToList();

        return Ok(result);
    }

    /// Converts raw plan limits/flags into human-readable Arabic feature strings
    /// that map 1-to-1 with what the design shows on the PlanCard component.
    private static IReadOnlyList<string> BuildFeaturesAr(Domain.Entities.Plan p)
    {
        var features = new List<string>();

        // ── Reads ─────────────────────────────────────────────────────────
        if (p.IndividualReadsLimit == -1)
            features.Add("وصول غير محدود لكافة التقارير");
        else if (p.IndividualReadsLimit > 0)
            features.Add($"وصول إلى {p.IndividualReadsLimit} تقرير شهرياً");

        // ── Search precision ──────────────────────────────────────────────
        if (p.AdvancedSearchPrecision == "high")
            features.Add("تحليلات متخصصة بدقة 95%");
        else
            features.Add("تحليلات عامة ومحدودة");

        // ── Sectors / advanced search ─────────────────────────────────────
        if (p.HasAdvancedSearch)
            features.Add("تحليل أكثر من 10 قطاعات");

        // ── AI compare ───────────────────────────────────────────────────
        if (p.AiCompareMaxReports > 0)
            features.Add($"مقارنات متعددة تصل إلى {p.AiCompareMaxReports} تقارير");

        // ── Support tier ─────────────────────────────────────────────────
        features.Add(p.SupportTier == "priority"
            ? "دعم فني متواصل 24/7"
            : "دعم عبر البريد الإلكتروني");

        // ── Saves ─────────────────────────────────────────────────────────
        if (p.IndividualSavedReportsLimit != 0)
            features.Add("حفظ التقارير المفضلة");

        // ── AI chat/summarize ─────────────────────────────────────────────
        if (p.AiAccessLevel is "individual_pro" or "org_basic" or "org_pro")
            features.Add("محادثة مع الذكاء الاصطناعي");

        // ── Org-specific ──────────────────────────────────────────────────
        if (p.UserLimit > 1)
            features.Add($"حساب رئيسي + {p.UserLimit - 1} مقاعد");

        // ── Interactions / sharing ────────────────────────────────────────
        if (p.HasInteractions)
            features.Add("مشاركة التقارير المجانية");

        // ── Notifications ─────────────────────────────────────────────────
        if (p.HasNotifications)
            features.Add("إشعارات تقارير جديدة");

        return features.AsReadOnly();
    }

    /// English mirror of BuildFeaturesAr. Order and conditions are kept
    /// identical so featuresAr[i] and featuresEn[i] describe the same
    /// capability — the SPA picks one based on the active locale.
    private static IReadOnlyList<string> BuildFeaturesEn(Domain.Entities.Plan p)
    {
        var features = new List<string>();

        if (p.IndividualReadsLimit == -1)
            features.Add("Unlimited access to all reports");
        else if (p.IndividualReadsLimit > 0)
            features.Add($"Access to {p.IndividualReadsLimit} reports per month");

        if (p.AdvancedSearchPrecision == "high")
            features.Add("Specialized analytics with 95% accuracy");
        else
            features.Add("General, limited analytics");

        if (p.HasAdvancedSearch)
            features.Add("Analysis across 10+ sectors");

        if (p.AiCompareMaxReports > 0)
            features.Add($"Multi-report comparisons up to {p.AiCompareMaxReports} reports");

        features.Add(p.SupportTier == "priority"
            ? "24/7 priority support"
            : "Email support");

        if (p.IndividualSavedReportsLimit != 0)
            features.Add("Save favorite reports");

        if (p.AiAccessLevel is "individual_pro" or "org_basic" or "org_pro")
            features.Add("Chat with the AI assistant");

        if (p.UserLimit > 1)
            features.Add($"Primary account + {p.UserLimit - 1} seats");

        if (p.HasInteractions)
            features.Add("Share free reports");

        if (p.HasNotifications)
            features.Add("New report notifications");

        return features.AsReadOnly();
    }
}
