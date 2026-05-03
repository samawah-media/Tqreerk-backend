using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Feature5_UsageTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "IndividualReadsLimit",
                table: "plans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "IndividualSavedReportsLimit",
                table: "plans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "usage_tracking",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActionType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    ConsumedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    BillingPeriodStart = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usage_tracking", x => x.Id);
                    table.ForeignKey(
                        name: "FK_usage_tracking_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_usage_tracking_subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalTable: "subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_usage_tracking_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_usage_tracking_OrganizationId",
                table: "usage_tracking",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_usage_tracking_SubscriptionId",
                table: "usage_tracking",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "ix_usage_tracking_user_action_period",
                table: "usage_tracking",
                columns: new[] { "UserId", "ActionType", "BillingPeriodStart" });

            migrationBuilder.CreateIndex(
                name: "ix_usage_tracking_user_consumed",
                table: "usage_tracking",
                columns: new[] { "UserId", "ConsumedAt" });

            // Seed the three individual plans referenced by Domain.Common.PlanIds.
            // -1 = unlimited, 0 = blocked. Free is what registration auto-links
            // to and what the backfill below assigns to legacy individual users.
            //
            // TargetType is stored as integer (enum ordinal) — see
            // PlanConfiguration; PlanTargetType.Individual = 0. Don't pass a
            // string here, Postgres won't cast it.
            //
            // ON CONFLICT DO NOTHING is defensive: if a plan with this ID was
            // inserted manually before the migration ran, we don't blow up.
            migrationBuilder.Sql("""
                INSERT INTO plans
                    ("Id", "NameAr", "NameEn", "TargetType", "AnnualPrice",
                     "UserLimit", "ReportsDownloadLimit", "AiCallsLimit",
                     "FeaturedReportsMonthly", "AiAccessLevel", "ApiAccess",
                     "IsActive", "IndividualReadsLimit", "IndividualSavedReportsLimit",
                     "CreatedAt")
                VALUES
                    ('70000000-0000-0000-0000-000000000001'::uuid,
                     'مجاني', 'Free', 0, 0,
                     1, 0, 0, 0, 'none', false, true,
                     3, 5,
                     now()),
                    ('70000000-0000-0000-0000-000000000002'::uuid,
                     'أساسي', 'Basic', 0, 99,
                     1, 0, 20, 0, 'basic', false, true,
                     30, 100,
                     now()),
                    ('70000000-0000-0000-0000-000000000003'::uuid,
                     'بريميوم', 'Premium', 0, 299,
                     1, 0, -1, 0, 'advanced', false, true,
                     -1, -1,
                     now())
                ON CONFLICT ("Id") DO NOTHING;
            """);

            // Backfill: every individual user (UserType = 'individual') that
            // currently has no Active subscription gets one against the free
            // plan. This is a one-shot — once the row exists, future runs
            // are no-ops thanks to the NOT EXISTS guard.
            //
            // We use gen_random_uuid() for the new subscription id and set
            // EndDate 100y in the future so SubscriptionStatus.Active is
            // self-consistent (free has no real billing cycle).
            migrationBuilder.Sql("""
                INSERT INTO subscriptions
                    ("Id", "UserId", "OrganizationId", "PlanId",
                     "Status", "PaymentStatus", "StartDate", "EndDate",
                     "CreatedAt")
                SELECT
                    gen_random_uuid(),
                    u."Id",
                    NULL,
                    '70000000-0000-0000-0000-000000000001'::uuid,
                    'Active',
                    'Paid',
                    now(),
                    now() + interval '100 years',
                    now()
                FROM users u
                WHERE u."UserType" = 'individual'
                  AND u."DeletedAt" IS NULL
                  AND NOT EXISTS (
                      SELECT 1 FROM subscriptions s
                      WHERE s."UserId" = u."Id"
                        AND s."Status" = 'Active'
                  );
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "usage_tracking");

            // Roll back the backfill (only the rows pointing at the free plan
            // we seeded — anything else is a real Subscription that must
            // survive a Down().)
            migrationBuilder.Sql("""
                DELETE FROM subscriptions
                WHERE "PlanId" = '70000000-0000-0000-0000-000000000001'::uuid;
            """);

            migrationBuilder.Sql("""
                DELETE FROM plans WHERE "Id" IN (
                    '70000000-0000-0000-0000-000000000001'::uuid,
                    '70000000-0000-0000-0000-000000000002'::uuid,
                    '70000000-0000-0000-0000-000000000003'::uuid
                );
            """);

            migrationBuilder.DropColumn(
                name: "IndividualReadsLimit",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "IndividualSavedReportsLimit",
                table: "plans");
        }
    }
}
