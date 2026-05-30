using System.IO.Compression;
using System.Text;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Npgsql;
using Taqreerk.API.Authorization;
using Taqreerk.Application.Interfaces;
using Taqreerk.Application.Services;
using Taqreerk.Application.Settings;
using Taqreerk.Infrastructure.Admin;
using Taqreerk.Infrastructure.AI;
using Taqreerk.Infrastructure.AI.Jobs;
using Taqreerk.Infrastructure.Data;
using Taqreerk.Infrastructure.Storage;

namespace Taqreerk.Extensions;

public static class ServiceExtensions
{
    public static IServiceCollection AddDatabase(this IServiceCollection services, IConfiguration config)
    {
        // Pool tuning: Npgsql defaults to MinPoolSize=0/MaxPoolSize=100 per
        // process. On Cloud Run that means every instance can independently
        // open 100 connections — multiplied across max-instances we'd blow
        // past Cloud SQL's connection cap. We cap at 20 per instance (more
        // than enough for our throughput) and keep 5 warm so the first
        // request after idle doesn't pay TCP+SSL handshake to Cloud SQL.
        // We only set these when not already specified in the connection
        // string so an operator can still override via env var.
        var raw = config.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");
        var builder = new NpgsqlConnectionStringBuilder(raw);
        if (!builder.ContainsKey("Pooling")) builder.Pooling = true;
        if (!builder.ContainsKey("Minimum Pool Size")) builder.MinPoolSize = 0;
        if (!builder.ContainsKey("Maximum Pool Size")) builder.MaxPoolSize = 5;
        if (!builder.ContainsKey("Connection Idle Lifetime")) builder.ConnectionIdleLifetime = 30;

        services.AddDbContext<TaqreerkDbContext>(options =>
            options.UseNpgsql(
                builder.ConnectionString,
                npgsql => npgsql.MigrationsAssembly(typeof(TaqreerkDbContext).Assembly.FullName)
            )
        );
        return services;
    }

    public static IServiceCollection AddPerformance(this IServiceCollection services)
    {
        // Response compression — JSON list payloads (e.g. /api/public/reports
        // with facets) easily reach 50-200 KB; on 4G that's 100-300 ms of
        // transfer cost alone. Brotli typically shrinks JSON by 80-90%.
        // EnableForHttps is required since Cloud Run terminates TLS at the LB
        // but inbound to Kestrel is HTTPS-equivalent (X-Forwarded-Proto=https).
        // BREACH risk is low: API responses don't reflect user-supplied
        // secrets back in plaintext bodies.
        services.AddResponseCompression(o =>
        {
            o.EnableForHttps = true;
            o.Providers.Add<BrotliCompressionProvider>();
            o.Providers.Add<GzipCompressionProvider>();
            // application/json is included in ResponseCompressionDefaults
            // already; we don't override the mime-types list.
        });
        // CompressionLevel.Fastest: for hot API paths the CPU cost of
        // Optimal (~5-10× more CPU) outweighs the marginal extra shrink.
        // We're optimizing TTFB, not bytes-on-disk.
        services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

        // Output caching — serves cached response bytes from in-process
        // memory on a hit, skipping the controller + DB entirely. Different
        // from [ResponseCache] which only writes Cache-Control headers and
        // depends on the client/CDN to honour them. We keep both: header for
        // CDN/browser, OutputCache for the first hit on each instance.
        // 64 MB cache is well within the 1 GB Cloud Run allotment and large
        // enough for hundreds of cached public-list responses.
        services.AddOutputCache(o =>
        {
            o.SizeLimit = 64L * 1024 * 1024;
            o.MaximumBodySize = 256L * 1024;
            o.DefaultExpirationTimeSpan = TimeSpan.FromMinutes(5);

            // Named policies referenced from [OutputCache(PolicyName=...)] on
            // anonymous-readable controllers. Keeping them here (instead of
            // duplicating Duration/VaryByQueryKeys per action) keeps the
            // cache key surface auditable in one place.
            o.AddPolicy("PublicList", b => b
                .Expire(TimeSpan.FromSeconds(60))
                .SetVaryByQuery(
                    "q", "sectors", "countries", "organizations",
                    "year_from", "year_to", "page_count_min", "page_count_max",
                    "language", "sort", "page", "pageSize"));
            o.AddPolicy("PublicFacets", b => b
                .Expire(TimeSpan.FromSeconds(60))
                .SetVaryByQuery(
                    "q", "sectors", "countries", "organizations",
                    "year_from", "year_to", "page_count_min", "page_count_max",
                    "language"));
            o.AddPolicy("PublicFeatured", b => b
                .Expire(TimeSpan.FromMinutes(5))
                .SetVaryByQuery("take", "section"));
            o.AddPolicy("PublicTrending", b => b
                .Expire(TimeSpan.FromMinutes(5))
                .SetVaryByQuery("take"));
            o.AddPolicy("PublicRecent", b => b
                .Expire(TimeSpan.FromMinutes(5))
                .SetVaryByQuery("take"));
            o.AddPolicy("PublicRelated", b => b
                .Expire(TimeSpan.FromMinutes(5))
                .SetVaryByQuery("take"));
            o.AddPolicy("PublicStats", b => b
                .Expire(TimeSpan.FromMinutes(5)));
            o.AddPolicy("Reference", b => b
                .Expire(TimeSpan.FromMinutes(10)));
        });

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
        services.Configure<GeminiSettings>(config.GetSection(GeminiSettings.Section));
        services.Configure<AdminWorkerSettings>(config.GetSection(AdminWorkerSettings.Section));
        services.Configure<QuotaSettings>(config.GetSection(QuotaSettings.Section));
        services.Configure<MoyasarSettings>(config.GetSection(MoyasarSettings.Section));

        services.AddMemoryCache();

        // Data protection: backs IEncryptionService for at-rest 2FA secrets.
        // Default key persistence (file system under %LOCALAPPDATA% in dev,
        // ContentRoot/keys in container images) is fine for now; switch to a
        // shared key ring before scaling to >1 admin host.
        services.AddDataProtection();

        // IHttpContextAccessor — needed by AdminActionLogger to capture
        // IP / UA from the current request. Cheap to register; safe even
        // for non-HTTP code paths (the accessor returns null context).
        services.AddHttpContextAccessor();

        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IAdminAuthService, AdminAuthService>();
        services.AddScoped<IAdminActionLogger, AdminActionLogger>();
        services.AddScoped<IRbacService, RbacService>();
        services.AddScoped<IStaffService, StaffService>();
        services.AddScoped<ITwoFactorService, TwoFactorService>();
        services.AddSingleton<IEncryptionService, DataProtectorEncryptionService>();
        services.AddScoped<IAdminDashboardService, AdminDashboardService>();
        services.AddScoped<IAdminOrganizationsService, AdminOrganizationsService>();
        services.AddScoped<IAdminUsersService, AdminUsersService>();
        services.AddScoped<IAdminCategoriesService, AdminCategoriesService>();
        services.AddScoped<IAdminPartnersService, AdminPartnersService>();
        services.AddScoped<IAdminFeaturedService, AdminFeaturedService>();
        services.AddScoped<IAdminPlansService, AdminPlansService>();
        services.AddScoped<IAdminStatsService, AdminStatsService>();
        services.AddScoped<IAdminSettingsService, AdminSettingsService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IOrganizationService, OrganizationService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IOrganizationAnalyticsService, OrganizationAnalyticsService>();
        services.AddScoped<IFeatureRequestsService, FeatureRequestsService>();
        services.AddScoped<ICompareService, CompareService>();
        services.AddScoped<IReportService, ReportService>();
        services.AddScoped<IPublicReportService, PublicReportService>();
        services.AddScoped<IReportInteractionsService, ReportInteractionsService>();
        services.AddScoped<IReportCommentsService, ReportCommentsService>();
        services.AddScoped<IReportAiService, ReportAiService>();
        services.AddScoped<IReviewService, ReviewService>();
        services.AddScoped<IQuotaService, QuotaService>();

        // Freemium gate, points ledger, "/me/*" surface and report
        // annotations / personal notes (Phase 1 features 1.1, 1.2a, 1.2b).
        // The [EnforceUsageLimit] action filter resolves IUsageService
        // per-request, so this MUST be Scoped — not Singleton.
        services.AddScoped<ISubscriptionProvisioningService, SubscriptionProvisioningService>();
        services.AddScoped<IUsageService, UsageService>();
        services.AddScoped<IPointsService, PointsService>();
        services.AddScoped<IMeService, MeService>();
        services.AddScoped<IAnnotationsService, AnnotationsService>();
        services.AddScoped<PaymentReceiptNotifier>();
        services.AddScoped<IPaymentCheckoutService, PaymentCheckoutService>();
        services.AddHttpClient<IMoyasarApiClient, MoyasarApiClient>();

        // Typed HttpClient for the external Python ai-service. Each call is a
        // long-running RPC (ingest can take minutes); the per-call timeout is
        // controlled via AiServiceSettings.TimeoutSeconds inside the client.
        services.AddHttpClient<IAiServiceClient, AiServiceClient>();

        // Typed HttpClient that talks DIRECTLY to Gemini for short-passage
        // translation (the PDF-reader selection toolbar). Bypasses the
        // ai-service to drop one Cloud-Run-to-Cloud-Run hop on a hot
        // interactive path. The other Gemini-backed flows (chat, ingest,
        // summarize, document translate) still proxy through ai-service
        // because they need the chunking + RAG + job-queue machinery there.
        services.AddHttpClient<IGeminiTextTranslator, GeminiTextTranslator>();

        // Bulk-import (admin Excel-driven third-party report ingestion).
        // The service handles parse + queue; Hangfire jobs pump rows through
        // fetch/upload/AI in the background with per-item retry and
        // crash-safe persistence.
        services.AddScoped<IBulkImportService, BulkImportService>();
        // Named HttpClient used by BulkUploadItemJob when fetching
        // arbitrary third-party PDFs from URLs in the uploaded Excel.
        // 5-min timeout rides out worst-case slow CDNs / large PDFs
        // without hanging the Hangfire worker indefinitely.
        services.AddHttpClient(BulkUploadItemJob.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "TaqreerkBulkImporter/1.0 (+https://taqreerk.com)");
        });

        // ── Hangfire ──────────────────────────────────────────────────────
        // Use a separate Npgsql pool for Hangfire so its polling connections
        // don't compete with EF Core queries on the MaxPoolSize=5 app pool.
        // Pool math per instance: 5 (EF Core) + 2 (Hangfire) = 7;
        // × 6 max-instances = 42 — within Cloud SQL's 50-connection cap.
        var hangfireConnStr = new NpgsqlConnectionStringBuilder(
            config.GetConnectionString("DefaultConnection")!)
        {
            MaxPoolSize = 2,
            ConnectionIdleLifetime = 30,
        }.ConnectionString;

        services.AddHangfire((sp, cfg) =>
            cfg.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
               .UseSimpleAssemblyNameTypeSerializer()
               .UseRecommendedSerializerSettings()
               .UsePostgreSqlStorage(
                   o => o.UseNpgsqlConnection(hangfireConnStr),
                   new Hangfire.PostgreSql.PostgreSqlStorageOptions
                   {
                       // Default is 30 min — too long after an OOM kill (SIGKILL).
                       // 5 min means stuck jobs resume within one poll cycle after
                       // a hard crash instead of blocking the queue for half an hour.
                       InvisibilityTimeout = TimeSpan.FromMinutes(5),
                       // Multiple Cloud Run instances each run a Hangfire server and
                       // compete for the DelayedJobScheduler lock. The default timeout
                       // is too short — instances that lose the race log an error and
                       // stop promoting scheduled jobs for up to 70 s. 30 s gives the
                       // lock holder enough time to finish its cycle before contenders
                       // give up and retry.
                       DistributedLockTimeout = TimeSpan.FromSeconds(30),
                   })
               // BulkJobFailedFilter marks items Failed when all Hangfire
               // retries are exhausted. Registered globally so it fires for
               // both bulk-upload and bulk-advance queues without needing
               // a per-class [BulkJobFailedFilter] attribute.
               .UseFilter(new BulkJobFailedFilter(sp)));

        // Two dedicated queues keep fast advance ticks (DB polls + short
        // AI calls) from queueing behind slow PDF downloads. WorkerCount=4
        // caps per-instance DB connection use to within the 5-slot EF pool.
        services.AddHangfireServer(opts =>
        {
            opts.Queues      = new[] { "bulk-upload", "bulk-advance", "default" };
            opts.WorkerCount = 4;
        });

        // Background worker that drains the ai_jobs queue. Single instance —
        // see ReportProcessingWorker.cs for scaling notes.
        // BulkImportProcessor (BackgroundService) is replaced by Hangfire
        // jobs (BulkUploadItemJob / BulkAdvanceItemJob). It is intentionally
        // NOT registered here — the Hangfire server above owns the pipeline.

        services.AddHostedService<ReportProcessingWorker>();

        // Background worker that releases stale review claims (>N min idle).
        services.AddHostedService<ClaimAutoReleaseWorker>();

        // Background worker that deactivates featured rows whose schedule
        // window has elapsed. One-minute poll, idempotent on retry.
        services.AddHostedService<FeaturedExpiryWorker>();

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

        // Pick the email sender based on configuration (priority: Graph > SMTP > dry-run log):
        //   - GraphTenantId set → Microsoft Graph API (GraphEmailSender) — preferred for M365
        //   - SmtpHost set      → real SMTP delivery (SmtpEmailSender)
        //   - neither set       → dry-run that logs the body to the console (LogEmailSender)
        var graphTenantId = config[$"{EmailSettings.Section}:GraphTenantId"];
        var smtpHost = config[$"{EmailSettings.Section}:SmtpHost"];
        if (!string.IsNullOrWhiteSpace(graphTenantId))
        {
            services.AddScoped<IEmailSender, GraphEmailSender>();
        }
        else if (!string.IsNullOrWhiteSpace(smtpHost))
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
