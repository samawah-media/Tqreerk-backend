using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Services;
using Taqreerk.Application.Settings;
using Taqreerk.Infrastructure.Data;

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

    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IAuthService, AuthService>();
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
