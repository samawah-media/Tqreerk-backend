using Taqreerk.Domain.Entities;

namespace Taqreerk.Infrastructure.Data.Seed;

/// Seed data for reference tables (countries, sectors). All IDs are deterministic
/// constants so HasData migrations stay reproducible across generations.
public static class ReferenceSeedData
{
    public static readonly DateTime SeedTime =
        DateTime.SpecifyKind(new DateTime(2026, 1, 1), DateTimeKind.Utc);

    // ── Countries ────────────────────────────────────────────────────────────
    // Arab countries first (primary audience), then a curated short list of
    // other major countries. Add more later via a follow-up seed migration.

    public static class CountryIds
    {
        public static readonly Guid SaudiArabia        = Guid.Parse("40000000-0000-0000-0000-000000000001");
        public static readonly Guid Uae                = Guid.Parse("40000000-0000-0000-0000-000000000002");
        public static readonly Guid Egypt              = Guid.Parse("40000000-0000-0000-0000-000000000003");
        public static readonly Guid Jordan             = Guid.Parse("40000000-0000-0000-0000-000000000004");
        public static readonly Guid Kuwait             = Guid.Parse("40000000-0000-0000-0000-000000000005");
        public static readonly Guid Qatar              = Guid.Parse("40000000-0000-0000-0000-000000000006");
        public static readonly Guid Bahrain            = Guid.Parse("40000000-0000-0000-0000-000000000007");
        public static readonly Guid Oman               = Guid.Parse("40000000-0000-0000-0000-000000000008");
        public static readonly Guid Lebanon            = Guid.Parse("40000000-0000-0000-0000-000000000009");
        public static readonly Guid Iraq               = Guid.Parse("40000000-0000-0000-0000-00000000000a");
        public static readonly Guid Syria              = Guid.Parse("40000000-0000-0000-0000-00000000000b");
        public static readonly Guid Palestine          = Guid.Parse("40000000-0000-0000-0000-00000000000c");
        public static readonly Guid Yemen              = Guid.Parse("40000000-0000-0000-0000-00000000000d");
        public static readonly Guid Morocco            = Guid.Parse("40000000-0000-0000-0000-00000000000e");
        public static readonly Guid Algeria            = Guid.Parse("40000000-0000-0000-0000-00000000000f");
        public static readonly Guid Tunisia            = Guid.Parse("40000000-0000-0000-0000-000000000010");
        public static readonly Guid Libya              = Guid.Parse("40000000-0000-0000-0000-000000000011");
        public static readonly Guid Sudan              = Guid.Parse("40000000-0000-0000-0000-000000000012");
        public static readonly Guid UnitedStates       = Guid.Parse("40000000-0000-0000-0000-000000000013");
        public static readonly Guid UnitedKingdom      = Guid.Parse("40000000-0000-0000-0000-000000000014");
        public static readonly Guid Germany            = Guid.Parse("40000000-0000-0000-0000-000000000015");
        public static readonly Guid France             = Guid.Parse("40000000-0000-0000-0000-000000000016");
        public static readonly Guid Turkey             = Guid.Parse("40000000-0000-0000-0000-000000000017");
        public static readonly Guid China              = Guid.Parse("40000000-0000-0000-0000-000000000018");
        public static readonly Guid India              = Guid.Parse("40000000-0000-0000-0000-000000000019");
    }

    public static IEnumerable<Country> Countries => new[]
    {
        new Country { Id = CountryIds.SaudiArabia,   NameAr = "السعودية",          NameEn = "Saudi Arabia",  IsoCode = "SA", CreatedAt = SeedTime },
        new Country { Id = CountryIds.Uae,           NameAr = "الإمارات",          NameEn = "UAE",           IsoCode = "AE", CreatedAt = SeedTime },
        new Country { Id = CountryIds.Egypt,         NameAr = "مصر",               NameEn = "Egypt",         IsoCode = "EG", CreatedAt = SeedTime },
        new Country { Id = CountryIds.Jordan,        NameAr = "الأردن",            NameEn = "Jordan",        IsoCode = "JO", CreatedAt = SeedTime },
        new Country { Id = CountryIds.Kuwait,        NameAr = "الكويت",            NameEn = "Kuwait",        IsoCode = "KW", CreatedAt = SeedTime },
        new Country { Id = CountryIds.Qatar,         NameAr = "قطر",               NameEn = "Qatar",         IsoCode = "QA", CreatedAt = SeedTime },
        new Country { Id = CountryIds.Bahrain,       NameAr = "البحرين",           NameEn = "Bahrain",       IsoCode = "BH", CreatedAt = SeedTime },
        new Country { Id = CountryIds.Oman,          NameAr = "عُمان",              NameEn = "Oman",          IsoCode = "OM", CreatedAt = SeedTime },
        new Country { Id = CountryIds.Lebanon,       NameAr = "لبنان",             NameEn = "Lebanon",       IsoCode = "LB", CreatedAt = SeedTime },
        new Country { Id = CountryIds.Iraq,          NameAr = "العراق",            NameEn = "Iraq",          IsoCode = "IQ", CreatedAt = SeedTime },
        new Country { Id = CountryIds.Syria,         NameAr = "سوريا",             NameEn = "Syria",         IsoCode = "SY", CreatedAt = SeedTime },
        new Country { Id = CountryIds.Palestine,     NameAr = "فلسطين",            NameEn = "Palestine",     IsoCode = "PS", CreatedAt = SeedTime },
        new Country { Id = CountryIds.Yemen,         NameAr = "اليمن",             NameEn = "Yemen",         IsoCode = "YE", CreatedAt = SeedTime },
        new Country { Id = CountryIds.Morocco,       NameAr = "المغرب",            NameEn = "Morocco",       IsoCode = "MA", CreatedAt = SeedTime },
        new Country { Id = CountryIds.Algeria,       NameAr = "الجزائر",           NameEn = "Algeria",       IsoCode = "DZ", CreatedAt = SeedTime },
        new Country { Id = CountryIds.Tunisia,       NameAr = "تونس",              NameEn = "Tunisia",       IsoCode = "TN", CreatedAt = SeedTime },
        new Country { Id = CountryIds.Libya,         NameAr = "ليبيا",             NameEn = "Libya",         IsoCode = "LY", CreatedAt = SeedTime },
        new Country { Id = CountryIds.Sudan,         NameAr = "السودان",           NameEn = "Sudan",         IsoCode = "SD", CreatedAt = SeedTime },
        new Country { Id = CountryIds.UnitedStates,  NameAr = "الولايات المتحدة",  NameEn = "United States", IsoCode = "US", CreatedAt = SeedTime },
        new Country { Id = CountryIds.UnitedKingdom, NameAr = "المملكة المتحدة",   NameEn = "United Kingdom",IsoCode = "GB", CreatedAt = SeedTime },
        new Country { Id = CountryIds.Germany,       NameAr = "ألمانيا",           NameEn = "Germany",       IsoCode = "DE", CreatedAt = SeedTime },
        new Country { Id = CountryIds.France,        NameAr = "فرنسا",             NameEn = "France",        IsoCode = "FR", CreatedAt = SeedTime },
        new Country { Id = CountryIds.Turkey,        NameAr = "تركيا",             NameEn = "Turkey",        IsoCode = "TR", CreatedAt = SeedTime },
        new Country { Id = CountryIds.China,         NameAr = "الصين",             NameEn = "China",         IsoCode = "CN", CreatedAt = SeedTime },
        new Country { Id = CountryIds.India,         NameAr = "الهند",             NameEn = "India",         IsoCode = "IN", CreatedAt = SeedTime },
    };

    // ── Sectors ──────────────────────────────────────────────────────────────

    public static class SectorIds
    {
        public static readonly Guid Economy        = Guid.Parse("50000000-0000-0000-0000-000000000001");
        public static readonly Guid Education      = Guid.Parse("50000000-0000-0000-0000-000000000002");
        public static readonly Guid Technology     = Guid.Parse("50000000-0000-0000-0000-000000000003");
        public static readonly Guid Investment     = Guid.Parse("50000000-0000-0000-0000-000000000004");
        public static readonly Guid Health         = Guid.Parse("50000000-0000-0000-0000-000000000005");
        public static readonly Guid Energy         = Guid.Parse("50000000-0000-0000-0000-000000000006");
        public static readonly Guid Environment    = Guid.Parse("50000000-0000-0000-0000-000000000007");
        public static readonly Guid Government     = Guid.Parse("50000000-0000-0000-0000-000000000008");
        public static readonly Guid SocialAffairs  = Guid.Parse("50000000-0000-0000-0000-000000000009");
        public static readonly Guid Culture        = Guid.Parse("50000000-0000-0000-0000-00000000000a");
        public static readonly Guid Media          = Guid.Parse("50000000-0000-0000-0000-00000000000b");
        public static readonly Guid Tourism        = Guid.Parse("50000000-0000-0000-0000-00000000000c");
        public static readonly Guid Industry       = Guid.Parse("50000000-0000-0000-0000-00000000000d");
        public static readonly Guid Agriculture    = Guid.Parse("50000000-0000-0000-0000-00000000000e");
        public static readonly Guid Telecom        = Guid.Parse("50000000-0000-0000-0000-00000000000f");
        public static readonly Guid Finance        = Guid.Parse("50000000-0000-0000-0000-000000000010");
        public static readonly Guid RealEstate     = Guid.Parse("50000000-0000-0000-0000-000000000011");
        public static readonly Guid Transportation = Guid.Parse("50000000-0000-0000-0000-000000000012");
    }

    public static IEnumerable<Sector> Sectors => new[]
    {
        new Sector { Id = SectorIds.Economy,        NameAr = "الاقتصاد",                NameEn = "Economy",            Slug = "economy",        IsActive = true, CreatedAt = SeedTime },
        new Sector { Id = SectorIds.Education,      NameAr = "التعليم",                  NameEn = "Education",          Slug = "education",      IsActive = true, CreatedAt = SeedTime },
        new Sector { Id = SectorIds.Technology,     NameAr = "التقنية",                  NameEn = "Technology",         Slug = "technology",     IsActive = true, CreatedAt = SeedTime },
        new Sector { Id = SectorIds.Investment,     NameAr = "الاستثمار",               NameEn = "Investment",         Slug = "investment",     IsActive = true, CreatedAt = SeedTime },
        new Sector { Id = SectorIds.Health,         NameAr = "الصحة",                    NameEn = "Health",             Slug = "health",         IsActive = true, CreatedAt = SeedTime },
        new Sector { Id = SectorIds.Energy,         NameAr = "الطاقة",                   NameEn = "Energy",             Slug = "energy",         IsActive = true, CreatedAt = SeedTime },
        new Sector { Id = SectorIds.Environment,    NameAr = "البيئة",                   NameEn = "Environment",        Slug = "environment",    IsActive = true, CreatedAt = SeedTime },
        new Sector { Id = SectorIds.Government,     NameAr = "الحكومة",                  NameEn = "Government",         Slug = "government",     IsActive = true, CreatedAt = SeedTime },
        new Sector { Id = SectorIds.SocialAffairs,  NameAr = "الشؤون الاجتماعية",       NameEn = "Social Affairs",     Slug = "social-affairs", IsActive = true, CreatedAt = SeedTime },
        new Sector { Id = SectorIds.Culture,        NameAr = "الثقافة",                  NameEn = "Culture",            Slug = "culture",        IsActive = true, CreatedAt = SeedTime },
        new Sector { Id = SectorIds.Media,          NameAr = "الإعلام",                  NameEn = "Media",              Slug = "media",          IsActive = true, CreatedAt = SeedTime },
        new Sector { Id = SectorIds.Tourism,        NameAr = "السياحة",                  NameEn = "Tourism",            Slug = "tourism",        IsActive = true, CreatedAt = SeedTime },
        new Sector { Id = SectorIds.Industry,       NameAr = "الصناعة",                  NameEn = "Industry",           Slug = "industry",       IsActive = true, CreatedAt = SeedTime },
        new Sector { Id = SectorIds.Agriculture,    NameAr = "الزراعة",                  NameEn = "Agriculture",        Slug = "agriculture",    IsActive = true, CreatedAt = SeedTime },
        new Sector { Id = SectorIds.Telecom,        NameAr = "الاتصالات",               NameEn = "Telecom",            Slug = "telecom",        IsActive = true, CreatedAt = SeedTime },
        new Sector { Id = SectorIds.Finance,        NameAr = "المالية",                  NameEn = "Finance",            Slug = "finance",        IsActive = true, CreatedAt = SeedTime },
        new Sector { Id = SectorIds.RealEstate,     NameAr = "العقارات",                 NameEn = "Real Estate",        Slug = "real-estate",    IsActive = true, CreatedAt = SeedTime },
        new Sector { Id = SectorIds.Transportation, NameAr = "النقل والمواصلات",        NameEn = "Transportation",     Slug = "transportation", IsActive = true, CreatedAt = SeedTime },
    };
}
