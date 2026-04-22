using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "countries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    NameAr = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NameEn = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsoCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_countries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "plans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    NameAr = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NameEn = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TargetType = table.Column<int>(type: "integer", nullable: false),
                    AnnualPrice = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    MiserPriceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    UserLimit = table.Column<int>(type: "integer", nullable: false),
                    ReportsDownloadLimit = table.Column<int>(type: "integer", nullable: false),
                    AiCallsLimit = table.Column<int>(type: "integer", nullable: false),
                    FeaturedReportsMonthly = table.Column<int>(type: "integer", nullable: false),
                    AiAccessLevel = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ApiAccess = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Permissions = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sectors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    NameAr = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NameEn = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sectors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    NameAr = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    NameEn = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Slug = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SectorScope = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CountryId = table.Column<Guid>(type: "uuid", nullable: true),
                    City = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    WebsiteUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LogoUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    IsPartner = table.Column<bool>(type: "boolean", nullable: false),
                    IsVerified = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organizations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_organizations_countries_CountryId",
                        column: x => x.CountryId,
                        principalTable: "countries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Phone = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    PasswordHash = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    FullName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UserType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "individual"),
                    JobTitle = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    InterestField = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    CountryId = table.Column<Guid>(type: "uuid", nullable: true),
                    EmailVerified = table.Column<bool>(type: "boolean", nullable: false),
                    PhoneVerified = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PreferredLanguage = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false, defaultValue: "ar"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_users_countries_CountryId",
                        column: x => x.CountryId,
                        principalTable: "countries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Payload = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_logs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_audit_logs_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "organization_files",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FileUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    UploadedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_files", x => x.Id);
                    table.ForeignKey(
                        name: "FK_organization_files_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "organization_profiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommercialRegisterNo = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LicenseDocumentUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IssuesReports = table.Column<bool>(type: "boolean", nullable: false),
                    AnnualReportsCount = table.Column<int>(type: "integer", nullable: false),
                    WantsToPublish = table.Column<bool>(type: "boolean", nullable: false),
                    InterestedInSubscription = table.Column<bool>(type: "boolean", nullable: false),
                    ContactPersonName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ContactPersonTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ContactEmail = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PoliciesAccepted = table.Column<bool>(type: "boolean", nullable: false),
                    PoliciesAcceptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_profiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_organization_profiles_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TitleAr = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    TitleEn = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    MessageAr = table.Column<string>(type: "text", nullable: true),
                    MessageEn = table.Column<string>(type: "text", nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    IsRead = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_notifications_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "organization_members",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    JoinedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_members", x => x.Id);
                    table.ForeignKey(
                        name: "FK_organization_members_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_organization_members_roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_organization_members_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DeviceInfo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    IsRevoked = table.Column<bool>(type: "boolean", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_refresh_tokens_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "reports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UploadedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SectorId = table.Column<Guid>(type: "uuid", nullable: true),
                    CountryId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Slug = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    ReportType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    OriginalLanguage = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false, defaultValue: "ar"),
                    PublicationYear = table.Column<int>(type: "integer", nullable: true),
                    PublicationDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PageCount = table.Column<int>(type: "integer", nullable: true),
                    FileUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CoverImageUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ExtractedText = table.Column<string>(type: "text", nullable: true),
                    SearchVector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true),
                    ViewsCount = table.Column<int>(type: "integer", nullable: false),
                    DownloadsCount = table.Column<int>(type: "integer", nullable: false),
                    AvgRating = table.Column<decimal>(type: "numeric(3,2)", precision: 3, scale: 2, nullable: false),
                    RatingsCount = table.Column<int>(type: "integer", nullable: false),
                    IsFeatured = table.Column<bool>(type: "boolean", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_reports_countries_CountryId",
                        column: x => x.CountryId,
                        principalTable: "countries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_reports_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reports_sectors_SectorId",
                        column: x => x.SectorId,
                        principalTable: "sectors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_reports_users_UploadedByUserId",
                        column: x => x.UploadedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "subscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    MiserSubscriptionId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    MiserCustomerId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PaymentStatus = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    StartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GracePeriodEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_subscriptions_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_subscriptions_plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_subscriptions_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_interests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SectorId = table.Column<Guid>(type: "uuid", nullable: true),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CountryId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_interests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_interests_countries_CountryId",
                        column: x => x.CountryId,
                        principalTable: "countries",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_user_interests_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "organizations",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_user_interests_sectors_SectorId",
                        column: x => x.SectorId,
                        principalTable: "sectors",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_user_interests_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ai_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: true),
                    JobType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    TokensUsed = table.Column<int>(type: "integer", nullable: false),
                    InputData = table.Column<string>(type: "jsonb", nullable: true),
                    OutputData = table.Column<string>(type: "jsonb", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ai_jobs_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ai_jobs_reports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ai_jobs_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "infographics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChartType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ChartData = table.Column<string>(type: "jsonb", nullable: true),
                    ExportUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_infographics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_infographics_reports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_infographics_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "report_keywords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    Keyword = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_keywords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_report_keywords_reports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "report_ratings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Rating = table.Column<int>(type: "integer", nullable: false),
                    Review = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_ratings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_report_ratings_reports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_report_ratings_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "report_recommendations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShareChannel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_recommendations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_report_recommendations_reports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_report_recommendations_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "report_views",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ViewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_views", x => x.Id);
                    table.ForeignKey(
                        name: "FK_report_views_reports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_report_views_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "saved_reports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    SavedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_saved_reports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_saved_reports_reports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_saved_reports_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false, defaultValue: "SAR"),
                    PaymentMethod = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    MiserPaymentReference = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PaidAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_payments_subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "report_ai_contents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    AiJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    Summary = table.Column<string>(type: "text", nullable: true),
                    Language = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    KeyFindings = table.Column<string>(type: "jsonb", nullable: true),
                    Recommendations = table.Column<string>(type: "jsonb", nullable: true),
                    Indicators = table.Column<string>(type: "jsonb", nullable: true),
                    Trends = table.Column<string>(type: "jsonb", nullable: true),
                    GeneratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_ai_contents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_report_ai_contents_ai_jobs_AiJobId",
                        column: x => x.AiJobId,
                        principalTable: "ai_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_report_ai_contents_reports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "report_comparisons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportIds = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    AiJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    ComparisonResult = table.Column<string>(type: "text", nullable: true),
                    SimilarityScore = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_comparisons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_report_comparisons_ai_jobs_AiJobId",
                        column: x => x.AiJobId,
                        principalTable: "ai_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_report_comparisons_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "report_translations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    AiJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    Language = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    TranslatedTitle = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TranslatedDescription = table.Column<string>(type: "text", nullable: true),
                    TranslatedSummary = table.Column<string>(type: "text", nullable: true),
                    TranslationStatus = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    TokensUsed = table.Column<int>(type: "integer", nullable: false),
                    TranslatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_translations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_report_translations_ai_jobs_AiJobId",
                        column: x => x.AiJobId,
                        principalTable: "ai_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_report_translations_reports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "invoices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    PdfUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IssuedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_invoices_payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_invoices_subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "roles",
                columns: new[] { "Id", "CreatedAt", "Description", "Name", "Permissions" },
                values: new object[,]
                {
                    { new Guid("00000000-0000-0000-0000-000000000001"), new DateTime(2026, 4, 21, 22, 48, 53, 633, DateTimeKind.Utc).AddTicks(243), "Organization administrator", "admin", null },
                    { new Guid("00000000-0000-0000-0000-000000000002"), new DateTime(2026, 4, 21, 22, 48, 53, 633, DateTimeKind.Utc).AddTicks(246), "Can upload and edit reports", "editor", null },
                    { new Guid("00000000-0000-0000-0000-000000000003"), new DateTime(2026, 4, 21, 22, 48, 53, 633, DateTimeKind.Utc).AddTicks(249), "Read-only access", "viewer", null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_jobs_OrganizationId",
                table: "ai_jobs",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_jobs_ReportId",
                table: "ai_jobs",
                column: "ReportId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_jobs_UserId",
                table: "ai_jobs",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_EntityId",
                table: "audit_logs",
                column: "EntityId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_EntityType",
                table: "audit_logs",
                column: "EntityType");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_OrganizationId",
                table: "audit_logs",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_countries_IsoCode",
                table: "countries",
                column: "IsoCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_infographics_CreatedByUserId",
                table: "infographics",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_infographics_ReportId",
                table: "infographics",
                column: "ReportId");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_InvoiceNumber",
                table: "invoices",
                column: "InvoiceNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invoices_PaymentId",
                table: "invoices",
                column: "PaymentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invoices_SubscriptionId",
                table: "invoices",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_notifications_UserId_IsRead",
                table: "notifications",
                columns: new[] { "UserId", "IsRead" });

            migrationBuilder.CreateIndex(
                name: "IX_organization_files_OrganizationId",
                table: "organization_files",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_organization_members_OrganizationId_UserId",
                table: "organization_members",
                columns: new[] { "OrganizationId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_organization_members_RoleId",
                table: "organization_members",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_organization_members_UserId",
                table: "organization_members",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_organization_profiles_OrganizationId",
                table: "organization_profiles",
                column: "OrganizationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_organizations_CountryId",
                table: "organizations",
                column: "CountryId");

            migrationBuilder.CreateIndex(
                name: "IX_organizations_Slug",
                table: "organizations",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payments_SubscriptionId",
                table: "payments",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_TokenHash",
                table: "refresh_tokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_UserId",
                table: "refresh_tokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_report_ai_contents_AiJobId",
                table: "report_ai_contents",
                column: "AiJobId");

            migrationBuilder.CreateIndex(
                name: "IX_report_ai_contents_ReportId_Language",
                table: "report_ai_contents",
                columns: new[] { "ReportId", "Language" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_report_comparisons_AiJobId",
                table: "report_comparisons",
                column: "AiJobId");

            migrationBuilder.CreateIndex(
                name: "IX_report_comparisons_UserId",
                table: "report_comparisons",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_report_keywords_ReportId_Keyword_Language",
                table: "report_keywords",
                columns: new[] { "ReportId", "Keyword", "Language" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_report_ratings_ReportId_UserId",
                table: "report_ratings",
                columns: new[] { "ReportId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_report_ratings_UserId",
                table: "report_ratings",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_report_recommendations_ReportId",
                table: "report_recommendations",
                column: "ReportId");

            migrationBuilder.CreateIndex(
                name: "IX_report_recommendations_UserId",
                table: "report_recommendations",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_report_translations_AiJobId",
                table: "report_translations",
                column: "AiJobId");

            migrationBuilder.CreateIndex(
                name: "IX_report_translations_ReportId_Language",
                table: "report_translations",
                columns: new[] { "ReportId", "Language" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_report_views_ReportId",
                table: "report_views",
                column: "ReportId");

            migrationBuilder.CreateIndex(
                name: "IX_report_views_UserId",
                table: "report_views",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_report_views_ViewedAt",
                table: "report_views",
                column: "ViewedAt");

            migrationBuilder.CreateIndex(
                name: "IX_reports_CountryId",
                table: "reports",
                column: "CountryId");

            migrationBuilder.CreateIndex(
                name: "IX_reports_OrganizationId",
                table: "reports",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_reports_SearchVector",
                table: "reports",
                column: "SearchVector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "IX_reports_SectorId",
                table: "reports",
                column: "SectorId");

            migrationBuilder.CreateIndex(
                name: "IX_reports_Slug",
                table: "reports",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reports_Status",
                table: "reports",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_reports_UploadedByUserId",
                table: "reports",
                column: "UploadedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_roles_Name",
                table: "roles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_saved_reports_ReportId",
                table: "saved_reports",
                column: "ReportId");

            migrationBuilder.CreateIndex(
                name: "IX_saved_reports_UserId_ReportId",
                table: "saved_reports",
                columns: new[] { "UserId", "ReportId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sectors_Slug",
                table: "sectors",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_OrganizationId",
                table: "subscriptions",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_PlanId",
                table: "subscriptions",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_UserId",
                table: "subscriptions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_user_interests_CountryId",
                table: "user_interests",
                column: "CountryId");

            migrationBuilder.CreateIndex(
                name: "IX_user_interests_OrganizationId",
                table: "user_interests",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_user_interests_SectorId",
                table: "user_interests",
                column: "SectorId");

            migrationBuilder.CreateIndex(
                name: "IX_user_interests_UserId",
                table: "user_interests",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_users_CountryId",
                table: "users",
                column: "CountryId");

            migrationBuilder.CreateIndex(
                name: "IX_users_Email",
                table: "users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_Phone",
                table: "users",
                column: "Phone",
                unique: true,
                filter: "\"Phone\" IS NOT NULL");

            // Full-text search trigger: keeps search_vector in sync with title, description, and extracted_text
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION reports_search_vector_update()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    NEW.search_vector :=
                        setweight(to_tsvector('arabic', coalesce(NEW."Title", '')), 'A') ||
                        setweight(to_tsvector('arabic', coalesce(NEW."Description", '')), 'B') ||
                        setweight(to_tsvector('arabic', coalesce(NEW."ExtractedText", '')), 'C') ||
                        setweight(to_tsvector('english', coalesce(NEW."Title", '')), 'A') ||
                        setweight(to_tsvector('english', coalesce(NEW."Description", '')), 'B') ||
                        setweight(to_tsvector('english', coalesce(NEW."ExtractedText", '')), 'C');
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER reports_search_vector_trigger
                BEFORE INSERT OR UPDATE ON reports
                FOR EACH ROW EXECUTE FUNCTION reports_search_vector_update();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "infographics");

            migrationBuilder.DropTable(
                name: "invoices");

            migrationBuilder.DropTable(
                name: "notifications");

            migrationBuilder.DropTable(
                name: "organization_files");

            migrationBuilder.DropTable(
                name: "organization_members");

            migrationBuilder.DropTable(
                name: "organization_profiles");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "report_ai_contents");

            migrationBuilder.DropTable(
                name: "report_comparisons");

            migrationBuilder.DropTable(
                name: "report_keywords");

            migrationBuilder.DropTable(
                name: "report_ratings");

            migrationBuilder.DropTable(
                name: "report_recommendations");

            migrationBuilder.DropTable(
                name: "report_translations");

            migrationBuilder.DropTable(
                name: "report_views");

            migrationBuilder.DropTable(
                name: "saved_reports");

            migrationBuilder.DropTable(
                name: "user_interests");

            migrationBuilder.DropTable(
                name: "payments");

            migrationBuilder.DropTable(
                name: "roles");

            migrationBuilder.DropTable(
                name: "ai_jobs");

            migrationBuilder.DropTable(
                name: "subscriptions");

            migrationBuilder.DropTable(
                name: "reports");

            migrationBuilder.DropTable(
                name: "plans");

            migrationBuilder.DropTable(
                name: "organizations");

            migrationBuilder.DropTable(
                name: "sectors");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "countries");

            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS reports_search_vector_trigger ON reports;
                DROP FUNCTION IF EXISTS reports_search_vector_update();
                """);
        }
    }
}
