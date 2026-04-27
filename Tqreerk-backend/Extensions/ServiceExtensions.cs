using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Taqreerk.API.Authorization;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Services;
using Taqreerk.Application.Settings;
using Taqreerk.Infrastructure.AI;
using Taqreerk.Infrastructure.Data;
using Taqreerk.Infrastructure.Storage;

namespace Taqreerk.Extensions;

public static class ServiceExtensions
{
    public static IServiceCollection AddDatabase(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<TaqreerkDbContext>(options =>
            options.UseNpgsql(
                config.GetConnectionString("DefaultConnection"),
                npgsql => npgsql.MigrationsAssembly(typeof(TaqreerkDbContext).Assembly.FullName)
            )
        );
        return services;
    }

    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services, IConfiguration config)
    {
        var jwtSection = config.GetSection(JwtSettings.Section);
        services.Configure<JwtSettings>(jwtSection);

        var jwt = jwtSection.Get<JwtSettings>()!;
        var key = Encoding.UTF8.GetBytes(jwt.SecretKey);

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(key),
                    ClockSkew = TimeSpan.Zero,
                };
            });

        return services;
    }

    public static IServiceCollection AddApplicationServices(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<EmailSettings>(config.GetSection(EmailSettings.Section));
        services.Configure<FileStorageSettings>(config.GetSection(FileStorageSettings.Section));
        services.Configure<AiServiceSettings>(config.GetSection(AiServiceSettings.Section));

        services.AddMemoryCache();

        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IRbacService, RbacService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IOrganizationService, OrganizationService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IReportService, ReportService>();
        services.AddScoped<IPublicReportService, PublicReportService>();
        services.AddScoped<IReportAiService, ReportAiService>();

        // Typed HttpClient for the external Python ai-service. Each call is a
        // long-running RPC (ingest can take minutes); the per-call timeout is
        // controlled via AiServiceSettings.TimeoutSeconds inside the client.
        services.AddHttpClient<IAiServiceClient, AiServiceClient>();

        // Background worker that drains the ai_jobs queue. Single instance —
        // see ReportProcessingWorker.cs for scaling notes.
        services.AddHostedService<ReportProcessingWorker>();

        // File storage: pick GCS when explicitly configured, otherwise local disk.
        // Same fall-through pattern as the email senders.
        var storageProvider = (config[$"{FileStorageSettings.Section}:Provider"] ?? "local").ToLowerInvariant();
        if (storageProvider == "gcs")
        {
            services.AddSingleton<IFileStorage, GcsFileStorage>();
        }
        else
        {
            services.AddSingleton<IFileStorage, LocalFileStorage>();
        }

        // Pick the email sender based on configuration:
        //   - SmtpHost set      → real SMTP delivery (SmtpEmailSender)
        //   - SmtpHost empty    → dry-run that logs the body to the console (LogEmailSender)
        // This way devs without SMTP credentials still see codes in their terminal.
        var smtpHost = config[$"{EmailSettings.Section}:SmtpHost"];
        if (!string.IsNullOrWhiteSpace(smtpHost))
        {
            services.AddScoped<IEmailSender, SmtpEmailSender>();
        }
        else
        {
            services.AddSingleton<IEmailSender, LogEmailSender>();
        }

        return services;
    }

    public static IServiceCollection AddPermissionAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddAuthorization();
        return services;
    }

    public static IServiceCollection AddSwaggerWithAuth(this IServiceCollection services)
    {
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Taqreerk API",
                Version = "v1",
                Description = "Arabic reports platform — Publisher/Consumer model",
            });

            var scheme = new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
                Description = "Enter your JWT token.",
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
            };

            c.AddSecurityDefinition("Bearer", scheme);
            c.AddSecurityRequirement(new OpenApiSecurityRequirement { { scheme, Array.Empty<string>() } });
        });

        return services;
    }
}
