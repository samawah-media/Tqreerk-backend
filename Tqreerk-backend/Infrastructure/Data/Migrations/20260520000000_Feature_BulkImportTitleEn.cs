using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Adds TitleEn to bulk_import_items so the Excel sheet can carry both
    /// the Arabic and English titles per row. Reports themselves are
    /// already bilingual (see Feature_ReportBilingualTitle); this migration
    /// lifts the per-row snapshot stored on the import item up to the same
    /// shape so the processor can copy each language into the Report
    /// verbatim instead of mirroring TitleAr into both arms.
    ///
    /// Legacy rows have their existing Title copied into TitleEn so the
    /// NOT NULL constraint holds; admins can refine those imports later
    /// from the bulk-import history UI.
    /// </summary>
    public partial class Feature_BulkImportTitleEn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TitleEn",
                table: "bulk_import_items",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.Sql(@"UPDATE bulk_import_items SET ""TitleEn"" = ""Title"" WHERE ""TitleEn"" IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "TitleEn",
                table: "bulk_import_items",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TitleEn",
                table: "bulk_import_items");
        }
    }
}
