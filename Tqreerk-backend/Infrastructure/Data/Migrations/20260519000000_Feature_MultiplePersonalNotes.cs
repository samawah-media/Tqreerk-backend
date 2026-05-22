using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Feature_MultiplePersonalNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Allow many notes per (user, report). Wipe the table first
            // because the unique index can't be dropped if duplicates
            // would violate it after re-creation (it can't, but truncate
            // also clears the old "single notepad" rows that used to be
            // edited in place and no longer match the new UX).
            migrationBuilder.Sql("TRUNCATE TABLE report_personal_notes;");

            migrationBuilder.DropIndex(
                name: "ix_report_personal_notes_user_report",
                table: "report_personal_notes");

            migrationBuilder.CreateIndex(
                name: "ix_report_personal_notes_user_report",
                table: "report_personal_notes",
                columns: new[] { "UserId", "ReportId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("TRUNCATE TABLE report_personal_notes;");

            migrationBuilder.DropIndex(
                name: "ix_report_personal_notes_user_report",
                table: "report_personal_notes");

            migrationBuilder.CreateIndex(
                name: "ix_report_personal_notes_user_report",
                table: "report_personal_notes",
                columns: new[] { "UserId", "ReportId" },
                unique: true);
        }
    }
}
