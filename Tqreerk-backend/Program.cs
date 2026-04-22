using Taqreerk.API.Middleware;
using Taqreerk.Extensions;

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

app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Taqreerk API v1"));
}

app.UseHttpsRedirection();
app.UseCors("DefaultCors");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
