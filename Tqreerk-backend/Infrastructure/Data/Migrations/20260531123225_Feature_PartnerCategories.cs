using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Feature_PartnerCategories : Migration
    {
        private static readonly Guid DefaultCategoryId =
            Guid.Parse("a0000000-0000-0000-0000-000000000001");

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PartnerCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NameAr = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NameEn = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartnerCategories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PartnerCategories_IsActive_SortOrder",
                table: "PartnerCategories",
                columns: new[] { "IsActive", "SortOrder" });

            migrationBuilder.InsertData(
                table: "PartnerCategories",
                columns: new[] { "Id", "NameAr", "NameEn", "IsActive", "SortOrder", "CreatedAt" },
                values: new object[]
                {
                    DefaultCategoryId, "شركاء عام", "General Partners", true, 0,
                    new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc)
                });

            migrationBuilder.AlterColumn<string>(
                name: "WebsiteUrl",
                table: "Partners",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "SortOrder",
                table: "Partners",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<string>(
                name: "NameEn",
                table: "Partners",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "NameAr",
                table: "Partners",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "LogoUrl",
                table: "Partners",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<bool>(
                name: "IsActive",
                table: "Partners",
                type: "boolean",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(bool),
                oldType: "boolean");

            migrationBuilder.AddColumn<Guid>(
                name: "CategoryId",
                table: "Partners",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql($"""
                UPDATE "Partners"
                SET "CategoryId" = '{DefaultCategoryId}'
                WHERE "CategoryId" IS NULL;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "CategoryId",
                table: "Partners",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Partners_CategoryId_IsActive_SortOrder",
                table: "Partners",
                columns: new[] { "CategoryId", "IsActive", "SortOrder" });

            migrationBuilder.AddForeignKey(
                name: "FK_Partners_PartnerCategories_CategoryId",
                table: "Partners",
                column: "CategoryId",
                principalTable: "PartnerCategories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Partners_PartnerCategories_CategoryId",
                table: "Partners");

            migrationBuilder.DropTable(
                name: "PartnerCategories");

            migrationBuilder.DropIndex(
                name: "IX_Partners_CategoryId_IsActive_SortOrder",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "CategoryId",
                table: "Partners");

            migrationBuilder.AlterColumn<string>(
                name: "WebsiteUrl",
                table: "Partners",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(2000)",
                oldMaxLength: 2000,
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "SortOrder",
                table: "Partners",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<string>(
                name: "NameEn",
                table: "Partners",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<string>(
                name: "NameAr",
                table: "Partners",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<string>(
                name: "LogoUrl",
                table: "Partners",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(1000)",
                oldMaxLength: 1000,
                oldNullable: true);

            migrationBuilder.AlterColumn<bool>(
                name: "IsActive",
                table: "Partners",
                type: "boolean",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: true);
        }
    }
}
