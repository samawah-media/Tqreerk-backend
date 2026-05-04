using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// EF wanted to recreate every "missing" table from earlier features
    /// because the local design DB the tooling targets is empty — but the
    /// real Staging/Production DBs already have those tables (pipeline
    /// applied them out-of-band). This migration is hand-trimmed to
    /// contain ONLY the new table for Feature 7 (report_feature_requests)
    /// + its indexes + its FKs.
    ///
    /// If you ever need to rebuild the local design DB from scratch,
    /// the previous migrations remain in this folder and apply cleanly
    /// in order — they just can't run again on the real DBs without
    /// being trimmed the same way.
    /// </remarks>
    public partial class Feature7_FeatureRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "report_feature_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecisionNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_feature_requests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_report_feature_requests_organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_report_feature_requests_reports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_report_feature_requests_users_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_report_feature_requests_users_ReviewedByUserId",
                        column: x => x.ReviewedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_report_feature_requests_org_created",
                table: "report_feature_requests",
                columns: new[] { "OrganizationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_report_feature_requests_RequestedByUserId",
                table: "report_feature_requests",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_report_feature_requests_ReviewedByUserId",
                table: "report_feature_requests",
                column: "ReviewedByUserId");

            migrationBuilder.CreateIndex(
                name: "ix_report_feature_requests_status_created",
                table: "report_feature_requests",
                columns: new[] { "Status", "CreatedAt" });

            // Partial unique index — Postgres-only. Enforces "at most one
            // Pending request per report" without blocking future
            // resubmissions after a Rejected decision.
            migrationBuilder.CreateIndex(
                name: "ux_report_feature_requests_pending_report",
                table: "report_feature_requests",
                column: "ReportId",
                unique: true,
                filter: "\"Status\" = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "report_feature_requests");
        }
    }
}
