using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;
using Taqreerk.Infrastructure.Data;

namespace Taqreerk.API.Filters;

/// Boolean feature-flag gate. Distinct from [EnforceUsageLimit] which
/// counts metered actions; this attribute checks whether the caller's
/// current plan even includes a given capability (e.g. Knowledge Graph,
/// Trend Analysis, Smart Alerts). Pro-only AI features are flagged here
/// — we don't meter them once a plan turns them on, so a counter would
/// be the wrong tool.
///
/// Usage:
///   [RequiresPlanFeature(nameof(Plan.HasKnowledgeGraph))]
///   public async Task&lt;IActionResult&gt; KnowledgeGraph(...) { ... }
///
/// On miss, throws PlanFeatureNotAvailableException → 403 with body
///   { code = "AI_FEATURE_NOT_AVAILABLE", featureName, currentPlanName }
/// the SPA renders a different upsell modal than the usage-limit one.
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequiresPlanFeatureAttribute : Attribute, IAsyncActionFilter
{
    private readonly string _featureName;

    /// <param name="featureName">
    ///   The Plan column name (use <c>nameof(Plan.HasKnowledgeGraph)</c>
    ///   so a rename surfaces as a compile error). The attribute resolves
    ///   it via reflection on the loaded Plan entity. Anything that's
    ///   not a public boolean property on Plan throws on first request
    ///   to make the misuse loud.
    /// </param>
    public RequiresPlanFeatureAttribute(string featureName)
    {
        if (string.IsNullOrWhiteSpace(featureName))
            throw new ArgumentException("Feature name is required.", nameof(featureName));
        _featureName = featureName;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var userIdClaim = context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            // Endpoint should be [Authorize]'d. Let the auth pipeline 401.
            await next();
            return;
        }

        var prop = typeof(Plan).GetProperty(_featureName,
            BindingFlags.Public | BindingFlags.Instance);
        if (prop is null || prop.PropertyType != typeof(bool))
        {
            // Misuse — fail loudly rather than silently let everyone
            // through. Caught at first request after deploy.
            throw new InvalidOperationException(
                $"[RequiresPlanFeature] '{_featureName}' is not a boolean property on Plan.");
        }

        var db = context.HttpContext.RequestServices.GetRequiredService<TaqreerkDbContext>();

        // Resolve the active subscription's plan. Same shape UsageService
        // uses, but we only need a couple of columns here. Using
        // AsNoTracking because we never write through this load.
        var plan = await db.Subscriptions
            .AsNoTracking()
            .Where(s => s.UserId == userId && s.Status == SubscriptionStatus.Active)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => s.Plan)
            .FirstOrDefaultAsync(context.HttpContext.RequestAborted);

        if (plan is null)
        {
            // Same posture as UsageService: no active subscription is a
            // misconfiguration (registration should auto-link to free).
            // We surface it as a 500-style InvalidOperation so the dev
            // sees the broken backfill, not a misleading 403.
            throw new InvalidOperationException(
                $"User {userId} has no active subscription; cannot evaluate plan feature '{_featureName}'.");
        }

        var enabled = (bool)(prop.GetValue(plan) ?? false);
        if (!enabled)
        {
            throw new PlanFeatureNotAvailableException(_featureName, plan.NameAr);
        }

        await next();
    }
}
