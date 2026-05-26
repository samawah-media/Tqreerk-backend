using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Adds the report_reviews table — one row per completed review action
    /// (approve / reject / return-for-edit). The claim itself stays on the
    /// Report row (ClaimedByReviewerId / ClaimedAt from PR A1); this table
    /// only holds the audit trail of finished decisions.
    /// </summary>
    public partial class Admin_ReportReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "report_reviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Decision = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ReviewNotes = table.Column<string>(type: "text", nullable: true),
                    InternalNotes = table.Column<string>(type: "text", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewDurationSeconds = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_report_reviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_report_reviews_reports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "reports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_report_reviews_users_ReviewerUserId",
                        column: x => x.ReviewerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_report_reviews_ReviewerUserId",
                table: "report_reviews",
                column: "ReviewerUserId");

            // Latest-first lookup. Used by the workspace history tab and the
            // org-side "what did the reviewer say last time" link. Descending
            // on CreatedAt avoids a sort step at query time.
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_report_reviews_ReportId_CreatedAt_desc\" " +
                "ON report_reviews (\"ReportId\" ASC, \"CreatedAt\" DESC);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "report_reviews");
        }
    }
}
