using System.Collections;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Filters;
using Taqreerk.Application.Interfaces;
using Taqreerk.Domain.Enums;

namespace Taqreerk.API.Filters;

/// Convenience attribute for endpoints that meter against the freemium
/// plan. Use this when the action is idempotent enough that we can split
/// "check the cap" from "record the usage" — the filter checks before
/// the action runs and records after the action returns 2xx.
///
/// For actions where the recording must roll back if the action throws
/// mid-flight (e.g. multi-step PDF generation), call
/// IUsageService.EnsureWithinLimitAndConsumeAsync directly inside the
/// service instead — that variant wraps the work in a single transaction.
///
/// Throws UsageLimitExceededException → 403 (handled in
/// ExceptionHandlingMiddleware).
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class EnforceUsageLimitAttribute : Attribute, IAsyncActionFilter
{
    private readonly UsageActionType _actionType;

    public EnforceUsageLimitAttribute(UsageActionType actionType)
    {
        _actionType = actionType;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var userIdClaim = context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            // Endpoint should be [Authorize]'d; if we get here without a
            // user we don't apply the gate (let the auth pipeline 401).
            await next();
            return;
        }

        var usage = context.HttpContext.RequestServices.GetRequiredService<IUsageService>();

        // Best-effort resourceId: if the action has a parameter named
        // "id" or "reportId" of type Guid, use it. Falls back to null.
        Guid? resourceId = null;
        foreach (var name in new[] { "id", "reportId" })
        {
            if (context.ActionArguments.TryGetValue(name, out var value) && value is Guid g)
            {
                resourceId = g;
                break;
            }
        }

        // Action-type-specific metadata captured for the activity feed.
        // For AiCompare we want the full list of report IDs (so the feed
        // can render "compared X with Y"); for AiTranslate we want the
        // target language. Anything else falls through with null metadata.
        var (rid, meta) = ExtractContext(_actionType, context.ActionArguments, resourceId);

        await usage.EnsureWithinLimitAndConsumeAsync(
            userId,
            _actionType,
            rid,
            async ct =>
            {
                var executed = await next();
                if (executed.Exception is not null && !executed.ExceptionHandled)
                {
                    // Bubble through so the outer transaction in
                    // EnsureWithinLimitAndConsume rolls back.
                    throw executed.Exception;
                }
            },
            context.HttpContext.RequestAborted,
            meta);
    }

    /// Pull resourceId + metadata out of the action's bound arguments for
    /// the action types that need richer context on the activity feed.
    /// Best-effort: failures fall back to (resourceId, null).
    private static (Guid? resourceId, string? metadata) ExtractContext(
        UsageActionType actionType,
        IDictionary<string, object?> args,
        Guid? defaultResourceId)
    {
        switch (actionType)
        {
            case UsageActionType.AiCompare:
            {
                // Body shape: { ReportIds: [guid, guid, ...] }. Pull the
                // list off the bound request object via reflection so we
                // don't have to reference the DTO type from this layer.
                var ids = FindGuidList(args, "ReportIds");
                if (ids is null || ids.Count == 0) return (defaultResourceId, null);
                var primary = ids[0];
                var meta = JsonSerializer.Serialize(new { reportIds = ids });
                return (primary, meta);
            }

            case UsageActionType.AiTranslate:
            {
                // Body shape may be { Text, TargetLanguage } (translate-text)
                // or { ReportId, TargetLanguage } (whole-document translate).
                var lang = FindString(args, "TargetLanguage");
                if (string.IsNullOrWhiteSpace(lang)) return (defaultResourceId, null);
                var meta = JsonSerializer.Serialize(new { targetLanguage = lang.Trim().ToLowerInvariant() });
                return (defaultResourceId, meta);
            }

            default:
                return (defaultResourceId, null);
        }
    }

    /// Walk the bound action arguments looking for a property named
    /// `propertyName` whose value is a list/array of Guids. Returns null
    /// when no match is found.
    private static IReadOnlyList<Guid>? FindGuidList(
        IDictionary<string, object?> args, string propertyName)
    {
        foreach (var arg in args.Values)
        {
            if (arg is null) continue;
            var prop = arg.GetType().GetProperty(propertyName);
            if (prop is null) continue;
            if (prop.GetValue(arg) is IEnumerable seq)
            {
                var list = new List<Guid>();
                foreach (var item in seq)
                {
                    if (item is Guid g) list.Add(g);
                }
                if (list.Count > 0) return list;
            }
        }
        return null;
    }

    /// Walk the bound action arguments looking for a string property
    /// named `propertyName`. Returns null when no match is found.
    private static string? FindString(
        IDictionary<string, object?> args, string propertyName)
    {
        foreach (var arg in args.Values)
        {
            if (arg is null) continue;
            var prop = arg.GetType().GetProperty(propertyName);
            if (prop is null) continue;
            if (prop.GetValue(arg) is string s && !string.IsNullOrWhiteSpace(s))
                return s;
        }
        return null;
    }
}
