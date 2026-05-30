using System.Text.Json.Serialization;
using Hangfire;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Taqreerk.API.Middleware;
using Taqreerk.Application.Settings;
using Taqreerk.Extensions;
using Taqreerk.Infrastructure.Data;
using Taqreerk.Infrastructure.Jobs;

var builder = WebApplication.CreateBuilder(args);

// ── Kestrel HTTP/2 ───────────────────────────────────────────────────────────
// Cloud Run sends h2c (cleartext HTTP/2) directly to the container when the
// service is deployed with --use-http2. Kestrel binds HTTP/1.1 only by
// default — without this it RSTs the h2c connection at the wire level and
// the LB returns 503 "remote refused stream reset" without ever touching
// our app. Setting Http1AndHttp2 on the endpoint defaults makes the same
// port speak both protocols (preface-detection on plain TCP, ALPN on TLS).
builder.WebHost.ConfigureKestrel(options =>
{
    options.ConfigureEndpointDefaults(listen =>
    {
        listen.Protocols = HttpProtocols.Http1AndHttp2;
    });
});

// ── Sentry ────────────────────────────────────────────────────────────────────
// Reads Sentry:* from configuration (appsettings / env vars: Sentry__Dsn, etc.).
// No-op when Dsn is empty, so local dev without a DSN stays silent.
builder.WebHost.UseSentry();

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddDatabase(builder.Configuration);
builder.Services.AddJwtAuthentication(builder.Configuration);
builder.Services.AddPermissionAuthorization();
builder.Services.AddApplicationServices(builder.Configuration);

builder.Services
    .AddControllers()
    // Accept enum DTOs as their symbolic string name (e.g. "Highlight"
    // instead of 0). Frontend ships enums by name, and DB columns store
    // them as strings via HasConversion<string>() — this keeps the wire
    // format consistent with both sides. Without this, MVC's default
    // System.Text.Json deserializer fails any enum field with a 400
    // model-binding error like "The JSON value could not be converted".
    .AddJsonOptions(o =>
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerWithAuth();

builder.Services.AddCors(options =>
    options.AddPolicy("DefaultCors", policy =>
    {
        var origins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? ["http://localhost:5173"];
        // "*" means any origin — used as a temporary opening before real
        // prod URLs are known. AllowAnyOrigin is incompatible with
        // AllowCredentials (browser spec), so cookies/Authorization headers
        // are NOT sent cross-origin in wildcard mode; switch to specific
        // origins once they're known.
        if (origins.Contains("*"))
            policy.AllowAnyOrigin();
        else
            policy.WithOrigins(origins).AllowCredentials();

        policy.AllowAnyHeader()
              .AllowAnyMethod()
              // Cache preflight responses for 2 h. Without this, browsers
              // emit an OPTIONS request before every cross-origin call —
              // on 4G that's an extra ~150 ms RTT per cold endpoint hit.
              // 2 h is below Chromium's 7200 s cap.
              .SetPreflightMaxAge(TimeSpan.FromHours(2));
    })
);

// Performance: response compression (Brotli/Gzip) + output caching.
// See AddPerformance for the rationale on each option.
builder.Services.AddPerformance();

// ── Pipeline ──────────────────────────────────────────────────────────────────
// Migrations are applied by the CI/CD pipeline before each deploy (see
// .github/workflows/deploy-*.yml). If /healthz reports pendingMigrations,
// that means the migration job did not run — investigate the pipeline.
var app = builder.Build();

// In Development, auto-apply pending migrations so devs don't have to run
// `dotnet ef database update` after pulling. CI still owns migrations in
// Staging/Production — this only runs locally.
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<TaqreerkDbContext>();
    await db.Database.MigrateAsync();
}

app.UseMiddleware<ExceptionHandlingMiddleware>();

// Compression runs as outer middleware so cached/static/dynamic responses
// alike get Brotli'd on the way out. Must be registered before any
// middleware that writes response bodies (auth, controllers).
app.UseResponseCompression();

if (!app.Environment.IsProduction())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Taqreerk API v1"));
}

// HTTPS redirection is intentionally NOT enabled. Cloud Run terminates TLS
// at its frontend LB and only exposes :443 externally — there is no http://
// path a client could reach our container with. UseHttpsRedirection here
// would inspect the proxied connection (which is plain HTTP to Kestrel)
// and try to 307 already-HTTPS clients back to themselves. Re-enable only
// if we move off Cloud Run and have to handle http:// ingress in-app.


// Serve files from the local-storage root when the LocalFileStorage provider is in use.
// In dev this lets the frontend access uploaded files via /uploads/* without GCS.
var fileStorage = app.Configuration.GetSection(FileStorageSettings.Section).Get<FileStorageSettings>()
    ?? new FileStorageSettings();
if ((fileStorage.Provider ?? "local").ToLowerInvariant() != "gcs")
{
    var rootPath = Path.IsPathRooted(fileStorage.LocalRoot)
        ? fileStorage.LocalRoot
        : Path.Combine(app.Environment.ContentRootPath, fileStorage.LocalRoot);
    Directory.CreateDirectory(rootPath);

    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(rootPath),
        RequestPath = fileStorage.LocalPublicBaseUrl.TrimEnd('/'),
    });
}

app.UseCors("DefaultCors");
app.UseAuthentication();
app.UseAuthorization();

// Hangfire dashboard — JWT + IsPlatformStaff. Browser: open once with
// /admin/hangfire?access_token=<staff-jwt> (sets hangfire_auth cookie);
// then use Recurring jobs / links normally. API: Authorization: Bearer <jwt>
app.UseHangfireDashboard("/admin/hangfire", new DashboardOptions
{
    Authorization = [new Taqreerk.API.Authorization.HangfirePlatformStaffFilter(app.Services)],
    IsReadOnlyFunc = _ => false,
});

// Maintenance gate. Sits after auth so the middleware can identify
// admin paths (it short-circuits everything except /api/admin/*,
// /api/auth/*, /healthz, /swagger and /uploads). The IsMaintenanceModeAsync
// read is cached for 30s in IMemoryCache so per-request overhead is
// effectively zero.
app.UseMiddleware<MaintenanceMiddleware>();

// Output cache must run after auth/maintenance (so cached responses don't
// bypass them) and before MapControllers (so the cache short-circuits
// controller execution on a hit). Endpoints opt-in via [OutputCache(...)]
// with a named PolicyName defined in AddPerformance().
app.UseOutputCache();

// Endpoints must be registered after UseAuthorization()
app.MapGet("/healthz", async (TaqreerkDbContext db) =>
{
    try
    {
        var canConnect = await db.Database.CanConnectAsync();
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();

        if (!canConnect)
            return Results.Json(new { status = "unhealthy", database = "unreachable" }, statusCode: 503);

        if (pending.Count > 0)
            return Results.Json(new { status = "unhealthy", database = "connected", pendingMigrations = pending }, statusCode: 503);

        return Results.Json(new { status = "healthy", database = "connected", pendingMigrations = Array.Empty<string>() });
    }
    catch (Exception ex)
    {
        return Results.Json(new { status = "unhealthy", error = ex.Message }, statusCode: 503);
    }
}).AllowAnonymous();


app.MapControllers();

// Annual auto-renewal: charge saved Moyasar tokens before EndDate (UTC 03:00 daily).
RecurringJob.AddOrUpdate<SubscriptionRenewalJob>(
    "subscription-auto-renewal",
    job => job.ExecuteAsync(CancellationToken.None),
    Cron.Daily(3));

app.Run();
