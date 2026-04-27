using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Feature3_SeedCountriesAndSectors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "IsActive",
                table: "sectors",
                type: "boolean",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(bool),
                oldType: "boolean");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAt",
                table: "sectors",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()",
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAt",
                table: "countries",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()",
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.InsertData(
                table: "countries",
                columns: new[] { "Id", "CreatedAt", "IsoCode", "NameAr", "NameEn" },
                values: new object[,]
                {
                    { new Guid("40000000-0000-0000-0000-000000000001"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "SA", "السعودية", "Saudi Arabia" },
                    { new Guid("40000000-0000-0000-0000-000000000002"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "AE", "الإمارات", "UAE" },
                    { new Guid("40000000-0000-0000-0000-000000000003"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "EG", "مصر", "Egypt" },
                    { new Guid("40000000-0000-0000-0000-000000000004"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "JO", "الأردن", "Jordan" },
                    { new Guid("40000000-0000-0000-0000-000000000005"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "KW", "الكويت", "Kuwait" },
                    { new Guid("40000000-0000-0000-0000-000000000006"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "QA", "قطر", "Qatar" },
                    { new Guid("40000000-0000-0000-0000-000000000007"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "BH", "البحرين", "Bahrain" },
                    { new Guid("40000000-0000-0000-0000-000000000008"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "OM", "عُمان", "Oman" },
                    { new Guid("40000000-0000-0000-0000-000000000009"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "LB", "لبنان", "Lebanon" },
                    { new Guid("40000000-0000-0000-0000-00000000000a"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "IQ", "العراق", "Iraq" },
                    { new Guid("40000000-0000-0000-0000-00000000000b"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "SY", "سوريا", "Syria" },
                    { new Guid("40000000-0000-0000-0000-00000000000c"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "PS", "فلسطين", "Palestine" },
                    { new Guid("40000000-0000-0000-0000-00000000000d"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "YE", "اليمن", "Yemen" },
                    { new Guid("40000000-0000-0000-0000-00000000000e"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "MA", "المغرب", "Morocco" },
                    { new Guid("40000000-0000-0000-0000-00000000000f"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "DZ", "الجزائر", "Algeria" },
                    { new Guid("40000000-0000-0000-0000-000000000010"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "TN", "تونس", "Tunisia" },
                    { new Guid("40000000-0000-0000-0000-000000000011"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "LY", "ليبيا", "Libya" },
                    { new Guid("40000000-0000-0000-0000-000000000012"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "SD", "السودان", "Sudan" },
                    { new Guid("40000000-0000-0000-0000-000000000013"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "US", "الولايات المتحدة", "United States" },
                    { new Guid("40000000-0000-0000-0000-000000000014"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "GB", "المملكة المتحدة", "United Kingdom" },
                    { new Guid("40000000-0000-0000-0000-000000000015"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "DE", "ألمانيا", "Germany" },
                    { new Guid("40000000-0000-0000-0000-000000000016"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "FR", "فرنسا", "France" },
                    { new Guid("40000000-0000-0000-0000-000000000017"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "TR", "تركيا", "Turkey" },
                    { new Guid("40000000-0000-0000-0000-000000000018"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "CN", "الصين", "China" },
                    { new Guid("40000000-0000-0000-0000-000000000019"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "IN", "الهند", "India" }
                });

            migrationBuilder.InsertData(
                table: "sectors",
                columns: new[] { "Id", "CreatedAt", "Description", "IsActive", "NameAr", "NameEn", "Slug" },
                values: new object[,]
                {
                    { new Guid("50000000-0000-0000-0000-000000000001"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "الاقتصاد", "Economy", "economy" },
                    { new Guid("50000000-0000-0000-0000-000000000002"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "التعليم", "Education", "education" },
                    { new Guid("50000000-0000-0000-0000-000000000003"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "التقنية", "Technology", "technology" },
                    { new Guid("50000000-0000-0000-0000-000000000004"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "الاستثمار", "Investment", "investment" },
                    { new Guid("50000000-0000-0000-0000-000000000005"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "الصحة", "Health", "health" },
                    { new Guid("50000000-0000-0000-0000-000000000006"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "الطاقة", "Energy", "energy" },
                    { new Guid("50000000-0000-0000-0000-000000000007"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "البيئة", "Environment", "environment" },
                    { new Guid("50000000-0000-0000-0000-000000000008"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "الحكومة", "Government", "government" },
                    { new Guid("50000000-0000-0000-0000-000000000009"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "الشؤون الاجتماعية", "Social Affairs", "social-affairs" },
                    { new Guid("50000000-0000-0000-0000-00000000000a"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "الثقافة", "Culture", "culture" },
                    { new Guid("50000000-0000-0000-0000-00000000000b"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "الإعلام", "Media", "media" },
                    { new Guid("50000000-0000-0000-0000-00000000000c"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "السياحة", "Tourism", "tourism" },
                    { new Guid("50000000-0000-0000-0000-00000000000d"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "الصناعة", "Industry", "industry" },
                    { new Guid("50000000-0000-0000-0000-00000000000e"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "الزراعة", "Agriculture", "agriculture" },
                    { new Guid("50000000-0000-0000-0000-00000000000f"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "الاتصالات", "Telecom", "telecom" },
                    { new Guid("50000000-0000-0000-0000-000000000010"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "المالية", "Finance", "finance" },
                    { new Guid("50000000-0000-0000-0000-000000000011"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "العقارات", "Real Estate", "real-estate" },
                    { new Guid("50000000-0000-0000-0000-000000000012"), new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), null, true, "النقل والمواصلات", "Transportation", "transportation" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000009"));

            migrationBuilder.DeleteData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-00000000000a"));

            migrationBuilder.DeleteData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-00000000000b"));

            migrationBuilder.DeleteData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-00000000000c"));

            migrationBuilder.DeleteData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-00000000000d"));

            migrationBuilder.DeleteData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-00000000000e"));

            migrationBuilder.DeleteData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-00000000000f"));

            migrationBuilder.DeleteData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000010"));

            migrationBuilder.DeleteData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000011"));

            migrationBuilder.DeleteData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000012"));

            migrationBuilder.DeleteData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000013"));

            migrationBuilder.DeleteData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000014"));

            migrationBuilder.DeleteData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000015"));

            migrationBuilder.DeleteData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000016"));

            migrationBuilder.DeleteData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000017"));

            migrationBuilder.DeleteData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000018"));

            migrationBuilder.DeleteData(
                table: "countries",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000019"));

            migrationBuilder.DeleteData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000002"));

            migrationBuilder.DeleteData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000003"));

            migrationBuilder.DeleteData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000004"));

            migrationBuilder.DeleteData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000005"));

            migrationBuilder.DeleteData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000006"));

            migrationBuilder.DeleteData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000007"));

            migrationBuilder.DeleteData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000008"));

            migrationBuilder.DeleteData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000009"));

            migrationBuilder.DeleteData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-00000000000a"));

            migrationBuilder.DeleteData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-00000000000b"));

            migrationBuilder.DeleteData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-00000000000c"));

            migrationBuilder.DeleteData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-00000000000d"));

            migrationBuilder.DeleteData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-00000000000e"));

            migrationBuilder.DeleteData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-00000000000f"));

            migrationBuilder.DeleteData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000010"));

            migrationBuilder.DeleteData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000011"));

            migrationBuilder.DeleteData(
                table: "sectors",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000012"));

            migrationBuilder.AlterColumn<bool>(
                name: "IsActive",
                table: "sectors",
                type: "boolean",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: true);

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAt",
                table: "sectors",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldDefaultValueSql: "now()");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CreatedAt",
                table: "countries",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldDefaultValueSql: "now()");
        }
    }
}
