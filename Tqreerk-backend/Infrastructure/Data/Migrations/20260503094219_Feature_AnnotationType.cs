using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Feature_AnnotationType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "SelectionText",
                table: "report_annotations",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000);

            migrationBuilder.AlterColumn<string>(
                name: "Note",
                table: "report_annotations",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000,
                oldNullable: true);

            // Default value backfills any pre-existing rows from
            // 1.2a/b/c — those were all drag-paint highlights from the
            // placeholder UI. The default is the column's only static
            // value; new inserts come from the service which sets Type
            // explicitly.
            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "report_annotations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Highlight");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Type",
                table: "report_annotations");

            migrationBuilder.AlterColumn<string>(
                name: "SelectionText",
                table: "report_annotations",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000,
                oldDefaultValue: "");

            migrationBuilder.AlterColumn<string>(
                name: "Note",
                table: "report_annotations",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(4000)",
                oldMaxLength: 4000,
                oldNullable: true);
        }
    }
}
