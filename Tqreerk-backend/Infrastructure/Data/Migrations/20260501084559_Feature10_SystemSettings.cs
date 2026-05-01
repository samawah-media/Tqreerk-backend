using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Feature10_SystemSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "system_settings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Key = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Value = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ValueType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_settings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "system_settings",
                columns: new[] { "Id", "Category", "CreatedAt", "Description", "IsSystem", "Key", "UpdatedAt", "Value", "ValueType" },
                values: new object[,]
                {
                    { new Guid("60000000-0000-0000-0000-000000000001"), "general", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "اسم المنصة الظاهر للمستخدمين.", true, "site_name", null, "تقريرك", "string" },
                    { new Guid("60000000-0000-0000-0000-000000000002"), "general", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "اللغة الافتراضية للواجهة (ar / en).", true, "default_language", null, "ar", "string" },
                    { new Guid("60000000-0000-0000-0000-000000000003"), "general", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "بريد دعم المنصة.", true, "support_email", null, "support@taqreerk.com", "string" },
                    { new Guid("60000000-0000-0000-0000-000000000004"), "limits", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "أقصى عدد تقارير شهريًا للجهات على الباقة المجانية.", true, "free_plan_reports_limit", null, "5", "int" },
                    { new Guid("60000000-0000-0000-0000-000000000005"), "limits", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "أقصى عدد طلبات ذكاء اصطناعي شهريًا للباقة المجانية.", true, "free_plan_ai_limit", null, "3", "int" },
                    { new Guid("60000000-0000-0000-0000-000000000006"), "reviews", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "بعد كم دقيقة يُعاد إصدار طلب مراجعة عالق.", true, "reviews.auto_release_minutes", null, "60", "int" },
                    { new Guid("60000000-0000-0000-0000-000000000007"), "reviews", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "أقصى عدد تقارير يستطيع مراجع فردي مطالبتها بالتوازي.", true, "reviews.reviewer_max_concurrent", null, "5", "int" },
                    { new Guid("60000000-0000-0000-0000-000000000008"), "ai", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "حرارة Gemini للملخصات والترجمات.", true, "ai.gemini_temperature", null, "0.4", "decimal" },
                    { new Guid("60000000-0000-0000-0000-000000000009"), "ai", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "أقصى عدد توكِن لكل استدعاء AI.", true, "ai.max_tokens", null, "4096", "int" },
                    { new Guid("60000000-0000-0000-0000-00000000000a"), "ai", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "عدد محاولات إعادة استدعاء AI عند الفشل.", true, "ai.retry_attempts", null, "3", "int" },
                    { new Guid("60000000-0000-0000-0000-00000000000b"), "featured", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "حد البطل الرئيسي.", true, "featured.max_homepage_hero", null, "3", "int" },
                    { new Guid("60000000-0000-0000-0000-00000000000c"), "featured", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "حد كاروسيل الصفحة الرئيسية.", true, "featured.max_carousel", null, "10", "int" },
                    { new Guid("60000000-0000-0000-0000-00000000000d"), "email", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "اسم المرسل في الإيميلات.", true, "email.sender_name", null, "تقريرك", "string" },
                    { new Guid("60000000-0000-0000-0000-00000000000e"), "email", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "عنوان الرد على الإيميلات.", true, "email.support_reply_to", null, "support@taqreerk.com", "string" },
                    { new Guid("60000000-0000-0000-0000-00000000000f"), "maintenance", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "وضع الصيانة. عند التفعيل يُحجب المستخدمون العاديون.", true, "maintenance.enabled", null, "false", "bool" },
                    { new Guid("60000000-0000-0000-0000-000000000010"), "maintenance", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "رسالة عرض الصيانة (تظهر للمستخدمين).", true, "maintenance.message", null, "المنصة تحت الصيانة، نعود قريبًا.", "string" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_system_settings_Category",
                table: "system_settings",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_system_settings_Key",
                table: "system_settings",
                column: "Key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "system_settings");
        }
    }
}
