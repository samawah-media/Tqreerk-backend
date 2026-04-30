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
                or Taqreerk.Application.Services.ForbiddenException))
            {
                SentrySdk.CaptureException(ex);
            }

            await HandleAsync(context, ex, _env.IsDevelopment());
        }
    }

    private static Task HandleAsync(HttpContext context, Exception ex, bool isDevelopment)
    {
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

        object body = isDevelopment && status == HttpStatusCode.InternalServerError
            ? new
            {
                status = (int)status,
                title,
                detail = ex.Message,
                exceptionType = ex.GetType().FullName,
                innerMessage = ex.InnerException?.Message,
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
}
