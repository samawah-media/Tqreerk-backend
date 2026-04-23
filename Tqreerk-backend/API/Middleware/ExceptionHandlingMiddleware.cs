using System.Net;
using System.Text.Json;
using Sentry;

namespace Taqreerk.API.Middleware;

public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
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
                or KeyNotFoundException or ArgumentException))
            {
                SentrySdk.CaptureException(ex);
            }

            await HandleAsync(context, ex);
        }
    }

    private static Task HandleAsync(HttpContext context, Exception ex)
    {
        var (status, title) = ex switch
        {
            InvalidOperationException => (HttpStatusCode.Conflict, ex.Message),
            UnauthorizedAccessException => (HttpStatusCode.Unauthorized, ex.Message),
            KeyNotFoundException => (HttpStatusCode.NotFound, ex.Message),
            ArgumentException => (HttpStatusCode.BadRequest, ex.Message),
            _ => (HttpStatusCode.InternalServerError, "An unexpected error occurred.")
        };

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)status;

        var body = JsonSerializer.Serialize(new
        {
            status = (int)status,
            title,
            traceId = context.TraceIdentifier
        });

        return context.Response.WriteAsync(body);
    }
}
