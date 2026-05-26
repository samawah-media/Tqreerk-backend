using System.Net;
using System.Text.Json;
using Sentry;
using Taqreerk.Application.Interfaces;

namespace Taqreerk.API.Middleware;

public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IWebHostEnvironment _env;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger, IWebHostEnvironment env)
    {
        _next = next;
        _logger = logger;
        _env = env;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception: {Message}", ex.Message);

            // Report only unexpected errors to Sentry; expected 4xx cases below aren't bugs.
            if (ex is not (InvalidOperationException or UnauthorizedAccessException
                or KeyNotFoundException or ArgumentException
                or QuotaExceededException
                or UsageLimitExceededException
                or PlanFeatureNotAvailableException
                or SubscriptionInactiveException
                or Taqreerk.Application.Services.ForbiddenException))
            {
                SentrySdk.CaptureException(ex);
            }

            await HandleAsync(context, ex, _env.IsDevelopment());
        }
    }

    private static Task HandleAsync(HttpContext context, Exception ex, bool isDevelopment)
    {
        // UsageLimitExceededException + PlanFeatureNotAvailableException
        // both map to 403 but ship a structured body the SPA reads:
        // `code` flips between the two so the frontend can pop the right
        // modal (counter cap → upgrade nag with reset date; feature flag
        // → "this plan doesn't include it"). Other 403s (bare
        // ForbiddenException) keep the lean shape.
        if (ex is UsageLimitExceededException usage)
        {
            return WriteJson(context, HttpStatusCode.Forbidden, isDevelopment, ex, new
            {
                status = (int)HttpStatusCode.Forbidden,
                title = ex.Message,
                code = "USAGE_LIMIT_EXCEEDED",
                actionType = usage.ActionType.ToString(),
                limit = usage.Limit,
                used = usage.Used,
                resetsAt = usage.ResetsAt,
                traceId = context.TraceIdentifier,
            });
        }

        if (ex is PlanFeatureNotAvailableException feature)
        {
            return WriteJson(context, HttpStatusCode.Forbidden, isDevelopment, ex, new
            {
                status = (int)HttpStatusCode.Forbidden,
                title = ex.Message,
                code = "AI_FEATURE_NOT_AVAILABLE",
                featureName = feature.FeatureName,
                currentPlanName = feature.CurrentPlanName,
                traceId = context.TraceIdentifier,
            });
        }

        if (ex is SubscriptionInactiveException)
        {
            return WriteJson(context, HttpStatusCode.Forbidden, isDevelopment, ex, new
            {
                status = (int)HttpStatusCode.Forbidden,
                title = ex.Message,
                code = "SUBSCRIPTION_INACTIVE",
                traceId = context.TraceIdentifier,
            });
        }

        var (status, title) = ex switch
        {
            Taqreerk.Application.Services.ForbiddenException => (HttpStatusCode.Forbidden, ex.Message),
            QuotaExceededException => (HttpStatusCode.TooManyRequests, ex.Message),
            InvalidOperationException => (HttpStatusCode.Conflict, ex.Message),
            UnauthorizedAccessException => (HttpStatusCode.Unauthorized, ex.Message),
            KeyNotFoundException => (HttpStatusCode.NotFound, ex.Message),
            ArgumentException => (HttpStatusCode.BadRequest, ex.Message),
            _ => (HttpStatusCode.InternalServerError, "An unexpected error occurred.")
        };

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)status;
        // Hint clients to back off until midnight UTC (cap reset). Coarse
        // value but sets the right shape for any retry-after-aware client.
        if (status == HttpStatusCode.TooManyRequests)
        {
            context.Response.Headers["Retry-After"] = "3600";
        }

        // In dev, expose the inner exception for ANY non-2xx so the generic
        // 409 "transient failure" message stops hiding the real EF/Npgsql
        // error. The lean prod payload is unchanged.
        object body = isDevelopment
            ? new
            {
                status = (int)status,
                title,
                detail = ex.Message,
                exceptionType = ex.GetType().FullName,
                innerMessage = ex.InnerException?.Message,
                innerType = ex.InnerException?.GetType().FullName,
                stackTrace = ex.StackTrace,
                traceId = context.TraceIdentifier,
            }
            : new
            {
                status = (int)status,
                title,
                traceId = context.TraceIdentifier,
            };

        return context.Response.WriteAsync(JsonSerializer.Serialize(body));
    }

    /// Helper for the structured-body 403s (usage limit / plan feature).
    /// Sets status + content-type and writes the canonical body. The dev
    /// payload merges in the inner-exception fields so a failure here
    /// still surfaces the underlying error in dev — production stays
    /// lean.
    private static Task WriteJson(
        HttpContext context, HttpStatusCode status, bool isDevelopment, Exception ex, object body)
    {
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)status;

        if (isDevelopment)
        {
            body = MergeDevFields(body, ex, context.TraceIdentifier);
        }

        return context.Response.WriteAsync(JsonSerializer.Serialize(body));
    }

    /// Reflection-based merge: copy the structured-body properties into
    /// a Dictionary so we can add the dev fields without losing the
    /// shape callers passed in. Production never calls this, so the
    /// reflection cost is dev-only.
    private static object MergeDevFields(object body, Exception ex, string traceId)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in body.GetType().GetProperties())
        {
            dict[prop.Name] = prop.GetValue(body);
        }
        dict["detail"] = ex.Message;
        dict["exceptionType"] = ex.GetType().FullName;
        dict["innerMessage"] = ex.InnerException?.Message;
        dict["innerType"] = ex.InnerException?.GetType().FullName;
        dict["stackTrace"] = ex.StackTrace;
        dict["traceId"] = traceId;
        return dict;
    }
}
