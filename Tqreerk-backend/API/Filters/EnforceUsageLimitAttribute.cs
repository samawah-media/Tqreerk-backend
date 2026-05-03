using System.Security.Claims;
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

        await usage.EnsureWithinLimitAndConsumeAsync(
            userId,
            _actionType,
            resourceId,
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
            context.HttpContext.RequestAborted);
    }
}
