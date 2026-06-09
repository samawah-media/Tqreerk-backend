using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Feature_BulkImportItemKeywords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Keywords",
                table: "bulk_import_items",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "system_settings",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000003"),
                column: "Value",
                value: "taqrerk@samawah1.sa");

            migrationBuilder.UpdateData(
                table: "system_settings",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-00000000000e"),
                column: "Value",
                value: "taqrerk@samawah1.sa");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Keywords",
                table: "bulk_import_items");

            migrationBuilder.UpdateData(
                table: "system_settings",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-000000000003"),
                column: "Value",
                value: "support@taqreerk.com");

            migrationBuilder.UpdateData(
                table: "system_settings",
                keyColumn: "Id",
                keyValue: new Guid("60000000-0000-0000-0000-00000000000e"),
                column: "Value",
                value: "support@taqreerk.com");
        }
    }
}
