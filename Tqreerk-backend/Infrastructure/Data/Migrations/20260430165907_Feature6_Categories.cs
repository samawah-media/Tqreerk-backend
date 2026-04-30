using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Feature6_Categories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "sectors",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "countries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000001"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000002"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000003"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000004"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000005"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000006"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000007"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000008"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000009"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-00000000000a"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-00000000000b"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-00000000000c"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-00000000000d"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-00000000000e"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-00000000000f"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000010"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000011"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000012"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000013"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000014"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000015"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000016"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000017"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000018"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000019"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.InsertData(
                table: "pages",
                columns: new[] { "Id", "CreatedAt", "Description", "IsSystem", "Key", "NameAr", "NameEn", "SortOrder", "UpdatedAt" },
                values: new object[] { new Guid("10000000-0000-0000-0000-00000000000c"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "categories", "التصنيفات", "Categories", 12, null });

            migrationBuilder.UpdateData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000001"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000002"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000003"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000004"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000005"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000006"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000007"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000008"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000009"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-00000000000a"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-00000000000b"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-00000000000c"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-00000000000d"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-00000000000e"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-00000000000f"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000010"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000011"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.UpdateData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000012"),
                column: "SortOrder",
                value: 0);

            migrationBuilder.InsertData(
                table: "permissions",
                columns: new[] { "Id", "CreatedAt", "Description", "IsSystem", "Key", "NameAr", "NameEn", "PageId", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("20000000-0000-0000-0000-000000000c01"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "view", "عرض", "View", new Guid("10000000-0000-0000-0000-00000000000c"), null },
                    { new Guid("20000000-0000-0000-0000-000000000c02"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "create", "إنشاء", "Create", new Guid("10000000-0000-0000-0000-00000000000c"), null },
                    { new Guid("20000000-0000-0000-0000-000000000c03"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "edit", "تعديل", "Edit", new Guid("10000000-0000-0000-0000-00000000000c"), null },
                    { new Guid("20000000-0000-0000-0000-000000000c04"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "delete", "حذف", "Delete", new Guid("10000000-0000-0000-0000-00000000000c"), null }
                });

            migrationBuilder.InsertData(
                table: "role_permissions",
                columns: new[] { "PermissionId", "RoleId", "CreatedAt" },
                values: new object[,]
                {
                    { new Guid("20000000-0000-0000-0000-000000000c01"), new Guid("30000000-0000-0000-0000-000000000001"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("20000000-0000-0000-0000-000000000c02"), new Guid("30000000-0000-0000-0000-000000000001"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("20000000-0000-0000-0000-000000000c03"), new Guid("30000000-0000-0000-0000-000000000001"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("20000000-0000-0000-0000-000000000c04"), new Guid("30000000-0000-0000-0000-000000000001"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc) }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "role_permissions",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { new Guid("20000000-0000-0000-0000-000000000c01"), new Guid("30000000-0000-0000-0000-000000000001") });

            migrationBuilder.DeleteData(
                table: "role_permissions",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { new Guid("20000000-0000-0000-0000-000000000c02"), new Guid("30000000-0000-0000-0000-000000000001") });

            migrationBuilder.DeleteData(
                table: "role_permissions",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { new Guid("20000000-0000-0000-0000-000000000c03"), new Guid("30000000-0000-0000-0000-000000000001") });

            migrationBuilder.DeleteData(
                table: "role_permissions",
                keyColumns: new[] { "PermissionId", "RoleId" },
                keyValues: new object[] { new Guid("20000000-0000-0000-0000-000000000c04"), new Guid("30000000-0000-0000-0000-000000000001") });

            migrationBuilder.DeleteData(
                table: "permissions",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000c01"));

            migrationBuilder.DeleteData(
                table: "permissions",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000c02"));

            migrationBuilder.DeleteData(
                table: "permissions",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000c03"));

            migrationBuilder.DeleteData(
                table: "permissions",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000c04"));

            migrationBuilder.DeleteData(
                table: "pages",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000c"));

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "sectors");

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "countries");
        }
    }
}
