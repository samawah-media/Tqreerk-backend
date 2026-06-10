using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixSoftDeleteUniqueIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_users_Email",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_reports_Slug",
                table: "reports");

            migrationBuilder.DropIndex(
                name: "IX_report_translations_ReportId_Language",
                table: "report_translations");

            migrationBuilder.DropIndex(
                name: "IX_report_ai_contents_ReportId_Language",
                table: "report_ai_contents");

            migrationBuilder.DropIndex(
                name: "IX_organizations_Slug",
                table: "organizations");

            migrationBuilder.CreateIndex(
                name: "IX_users_Email",
                table: "users",
                column: "Email",
                unique: true,
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_reports_Slug",
                table: "reports",
                column: "Slug",
                unique: true,
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_report_translations_ReportId_Language",
                table: "report_translations",
                columns: new[] { "ReportId", "Language" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_report_ai_contents_ReportId_Language",
                table: "report_ai_contents",
                columns: new[] { "ReportId", "Language" },
                unique: true,
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_organizations_Slug",
                table: "organizations",
                column: "Slug",
                unique: true,
                filter: "\"DeletedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_users_Email",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_reports_Slug",
                table: "reports");

            migrationBuilder.DropIndex(
                name: "IX_report_translations_ReportId_Language",
                table: "report_translations");

            migrationBuilder.DropIndex(
                name: "IX_report_ai_contents_ReportId_Language",
                table: "report_ai_contents");

            migrationBuilder.DropIndex(
                name: "IX_organizations_Slug",
                table: "organizations");

            migrationBuilder.CreateIndex(
                name: "IX_users_Email",
                table: "users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reports_Slug",
                table: "reports",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_report_translations_ReportId_Language",
                table: "report_translations",
                columns: new[] { "ReportId", "Language" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_report_ai_contents_ReportId_Language",
                table: "report_ai_contents",
                columns: new[] { "ReportId", "Language" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_organizations_Slug",
                table: "organizations",
                column: "Slug",
                unique: true);
        }
    }
}
