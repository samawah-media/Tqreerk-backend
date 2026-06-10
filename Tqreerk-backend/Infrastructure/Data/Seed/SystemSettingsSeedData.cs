using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Seed;

/// Default values for system_settings, applied via HasData. Stable Guids
/// keep the migration deterministic across regenerations. Adding a new
/// setting is a one-line change here + a new migration.
public static class SystemSettingsSeedData
{
    /// Match RbacSeedData's seed timestamp so migrations stay deterministic.
    private static readonly DateTime SeedTime =
        DateTime.SpecifyKind(new DateTime(2026, 1, 1), DateTimeKind.Utc);

    /// Single row's Guid is derived from a fixed prefix + a 2-hex slot so
    /// inserting a new setting in the middle of the list doesn't shift
    /// existing IDs. Prefix `60000000` is reserved for system_settings —
    /// see the other seed files for the prefix map.
    private static Guid SettingId(int slot) =>
        Guid.Parse($"60000000-0000-0000-0000-00000000{slot:x4}");

    private record SettingSpec(string Key, string Category, string ValueType, string Default, string DescAr);

    private static readonly SettingSpec[] Specs =
    [
        // General
        new("site_name",                  "general",     "string", "تقريرك",                            "اسم المنصة الظاهر للمستخدمين."),
        new("default_language",           "general",     "string", "ar",                                "اللغة الافتراضية للواجهة (ar / en)."),
        new("support_email",              "general",     "string", "taqrerk@samawah1.sa",               "بريد دعم المنصة."),

        // Limits
        new("free_plan_reports_limit",    "limits",      "int",    "5",                                 "أقصى عدد تقارير شهريًا للجهات على الباقة المجانية."),
        new("free_plan_ai_limit",         "limits",      "int",    "3",                                 "أقصى عدد طلبات ذكاء اصطناعي شهريًا للباقة المجانية."),

        // Reviews
        new("reviews.auto_release_minutes",   "reviews", "int",    "60",                                "بعد كم دقيقة يُعاد إصدار طلب مراجعة عالق."),
        new("reviews.reviewer_max_concurrent","reviews", "int",    "5",                                 "أقصى عدد تقارير يستطيع مراجع فردي مطالبتها بالتوازي."),

        // AI
        new("ai.gemini_temperature",      "ai",          "decimal","0.4",                               "حرارة Gemini للملخصات والترجمات."),
        new("ai.max_tokens",              "ai",          "int",    "4096",                              "أقصى عدد توكِن لكل استدعاء AI."),
        new("ai.retry_attempts",          "ai",          "int",    "3",                                 "عدد محاولات إعادة استدعاء AI عند الفشل."),

        // Featured
        new("featured.max_homepage_hero", "featured",    "int",    "4",                                 "حد التدفق المعرفي."),
        new("featured.max_carousel",      "featured",    "int",    "4",                                 "حد التقارير الأكثر رواجاً."),

        // Email
        new("email.sender_name",          "email",       "string", "تقريرك",                            "اسم المرسل في الإيميلات."),
        new("email.support_reply_to",     "email",       "string", "taqrerk@samawah1.sa",               "عنوان الرد على الإيميلات."),

        // Maintenance — flag is OFF by default. Turning it on blocks
        // public traffic via MaintenanceMiddleware.
        new("maintenance.enabled",        "maintenance", "bool",   "false",                             "وضع الصيانة. عند التفعيل يُحجب المستخدمون العاديون."),
        new("maintenance.message",        "maintenance", "string", "المنصة تحت الصيانة، نعود قريبًا.",   "رسالة عرض الصيانة (تظهر للمستخدمين)."),
    ];

    public static IEnumerable<SystemSetting> DefaultSettings =>
        Specs.Select((s, i) => new SystemSetting
        {
            Id = SettingId(i + 1),
            Key = s.Key,
            Category = s.Category,
            ValueType = s.ValueType,
            Value = s.Default,
            Description = s.DescAr,
            IsSystem = true,
            CreatedAt = SeedTime,
        });
}
