using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Taqreerk.API.Middleware;
using Taqreerk.Application.Settings;
using Taqreerk.Extensions;
using Taqreerk.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

// ── Sentry ────────────────────────────────────────────────────────────────────
// Reads Sentry:* from configuration (appsettings / env vars: Sentry__Dsn, etc.).
// No-op when Dsn is empty, so local dev without a DSN stays silent.
builder.WebHost.UseSentry();

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddDatabase(builder.Configuration);
builder.Services.AddJwtAuthentication(builder.Configuration);
builder.Services.AddPermissionAuthorization();
builder.Services.AddApplicationServices(builder.Configuration);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerWithAuth();

builder.Services.AddCors(options =>
    options.AddPolicy("DefaultCors", policy =>
        policy.WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? ["http://localhost:5173"])
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()
    )
);

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

if (!app.Environment.IsProduction())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Taqreerk API v1"));
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}


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

app.Run();
