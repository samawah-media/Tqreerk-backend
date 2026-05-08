using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Hand-trimmed: EF's auto-generated diff tried to RENAME
    /// `ApiAccess` (bool) → `HasTrendAnalysis` (bool) and
    /// `AiCallsLimit` (int) → `ReportsUploadLimit` (int) because the
    /// model dropped the old names and added new same-typed columns.
    /// That would carry the wrong semantics across (e.g. paid orgs
    /// flagged as having TrendAnalysis just because they had API
    /// access). We override with explicit DropColumn + AddColumn so
    /// every new column starts from its real default, and we keep the
    /// `Authors` / `Source` additions on `reports` that came in
    /// alongside this work.
    ///
    /// Plan rows lose their `AiCallsLimit` value. The seed script
    /// re-populates the new per-action AI limits (PLANS_SEED.sql
    /// in the frontend repo).
    /// </remarks>
    public partial class Plan_TierSchemaExpansion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── Subscriptions: add-on bag ──────────────────────────────
            migrationBuilder.AddColumn<string>(
                name: "AddonsJson",
                table: "subscriptions",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            // ── Reports: companion fields landed in the same edit set ──
            migrationBuilder.AddColumn<string>(
                name: "Authors",
                table: "reports",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "reports",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            // ── Plans: drop the legacy single-column AI cap + ApiAccess
            // bool. The seed script re-populates the new columns; no
            // existing paid customers depend on these values yet.
            migrationBuilder.DropColumn(
                name: "AiCallsLimit",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "ApiAccess",
                table: "plans");

            // ── Plans: per-action AI counters ──────────────────────────
            migrationBuilder.AddColumn<int>(
                name: "AiSummarizeLimit",
                table: "plans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AiKeyFindingsLimit",
                table: "plans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AiTranslateLimit",
                table: "plans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AiSimilarSuggestionsLimit",
                table: "plans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AiCompareLimit",
                table: "plans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AiCompareMaxReports",
                table: "plans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // ── Plans: org-side counters ───────────────────────────────
            migrationBuilder.AddColumn<int>(
                name: "ReportsUploadLimit",
                table: "plans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "IndividualDownloadsLimit",
                table: "plans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // ── Plans: tier labels (string enums) ──────────────────────
            // Defaults match the entity defaults so existing rows land
            // on the conservative tier when the seed hasn't repopulated
            // them yet.
            migrationBuilder.AddColumn<string>(
                name: "AdvancedSearchPrecision",
                table: "plans",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "standard");

            migrationBuilder.AddColumn<string>(
                name: "OrgPageTier",
                table: "plans",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "basic");

            migrationBuilder.AddColumn<string>(
                name: "SupportTier",
                table: "plans",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "email");

            migrationBuilder.AddColumn<string>(
                name: "DashboardTier",
                table: "plans",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "standard");

            migrationBuilder.AddColumn<string>(
                name: "NotificationsTier",
                table: "plans",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "none");

            migrationBuilder.AddColumn<string>(
                name: "UpdatesCadence",
                table: "plans",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "monthly");

            // ── Plans: feature-flag booleans ───────────────────────────
            migrationBuilder.AddColumn<bool>(
                name: "HasNotifications",
                table: "plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasAdvancedSearch",
                table: "plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasInteractions",
                table: "plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasExclusiveContent",
                table: "plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasTrendAnalysis",
                table: "plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasIndicatorExtraction",
                table: "plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasSmartRecommendations",
                table: "plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasKnowledgeGraph",
                table: "plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasSmartAlerts",
                table: "plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasOpportunityDiscovery",
                table: "plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasSectoralAnalysis",
                table: "plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse the additions then re-add the dropped legacy columns
            // so the schema returns to the prior shape. Existing rows
            // can't recover their original AiCallsLimit / ApiAccess values
            // — Down is a structural rollback only.
            migrationBuilder.DropColumn(name: "AddonsJson", table: "subscriptions");

            migrationBuilder.DropColumn(name: "Authors", table: "reports");
            migrationBuilder.DropColumn(name: "Source", table: "reports");

            migrationBuilder.DropColumn(name: "AiSummarizeLimit", table: "plans");
            migrationBuilder.DropColumn(name: "AiKeyFindingsLimit", table: "plans");
            migrationBuilder.DropColumn(name: "AiTranslateLimit", table: "plans");
            migrationBuilder.DropColumn(name: "AiSimilarSuggestionsLimit", table: "plans");
            migrationBuilder.DropColumn(name: "AiCompareLimit", table: "plans");
            migrationBuilder.DropColumn(name: "AiCompareMaxReports", table: "plans");
            migrationBuilder.DropColumn(name: "ReportsUploadLimit", table: "plans");
            migrationBuilder.DropColumn(name: "IndividualDownloadsLimit", table: "plans");
            migrationBuilder.DropColumn(name: "AdvancedSearchPrecision", table: "plans");
            migrationBuilder.DropColumn(name: "OrgPageTier", table: "plans");
            migrationBuilder.DropColumn(name: "SupportTier", table: "plans");
            migrationBuilder.DropColumn(name: "DashboardTier", table: "plans");
            migrationBuilder.DropColumn(name: "NotificationsTier", table: "plans");
            migrationBuilder.DropColumn(name: "UpdatesCadence", table: "plans");
            migrationBuilder.DropColumn(name: "HasNotifications", table: "plans");
            migrationBuilder.DropColumn(name: "HasAdvancedSearch", table: "plans");
            migrationBuilder.DropColumn(name: "HasInteractions", table: "plans");
            migrationBuilder.DropColumn(name: "HasExclusiveContent", table: "plans");
            migrationBuilder.DropColumn(name: "HasTrendAnalysis", table: "plans");
            migrationBuilder.DropColumn(name: "HasIndicatorExtraction", table: "plans");
            migrationBuilder.DropColumn(name: "HasSmartRecommendations", table: "plans");
            migrationBuilder.DropColumn(name: "HasKnowledgeGraph", table: "plans");
            migrationBuilder.DropColumn(name: "HasSmartAlerts", table: "plans");
            migrationBuilder.DropColumn(name: "HasOpportunityDiscovery", table: "plans");
            migrationBuilder.DropColumn(name: "HasSectoralAnalysis", table: "plans");

            migrationBuilder.AddColumn<int>(
                name: "AiCallsLimit",
                table: "plans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "ApiAccess",
                table: "plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
