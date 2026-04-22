using Microsoft.EntityFrameworkCore;
using Taqreerk.API.Middleware;
using Taqreerk.Extensions;
using Taqreerk.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddDatabase(builder.Configuration);
builder.Services.AddJwtAuthentication(builder.Configuration);
builder.Services.AddApplicationServices();

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
var app = builder.Build();

// Apply pending EF migrations on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TaqreerkDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<TaqreerkDbContext>>();
    try
    {
        db.Database.Migrate();
        logger.LogInformation("Database migrations applied successfully.");
    }
    catch (Exception ex)
    {
        // Log and continue — app still starts so Cloud Run health check passes.
        // Fix the DB connection and redeploy to apply migrations.
        logger.LogError(ex, "Failed to apply database migrations on startup.");
    }
}

app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Taqreerk API v1"));
}

// Registered before HTTPS redirect so Cloud Run's plain-HTTP probes reach it
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
});

app.UseHttpsRedirection();
app.UseCors("DefaultCors");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
