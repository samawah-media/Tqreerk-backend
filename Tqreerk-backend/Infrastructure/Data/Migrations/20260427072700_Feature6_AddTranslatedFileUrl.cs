using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Adds TranslatedFileUrl on report_translations. The ai-service /translate
    /// endpoint returns a GCS URL pointing at the translated PDF (Google Cloud
    /// Translation v3 Document Translation), so we need a column to persist it.
    /// </summary>
    public partial class Feature6_AddTranslatedFileUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TranslatedFileUrl",
                table: "report_translations",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TranslatedFileUrl",
                table: "report_translations");
        }
    }
}
