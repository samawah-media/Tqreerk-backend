using Taqreerk.Domain.Entities;
using Taqreerk.Domain.Enums;

namespace Taqreerk.Infrastructure.Data.Seed;

/// Seed data for Pages, Permissions, Roles and RolePermission grants.
/// Referenced by EF configurations via HasData. All timestamps are a fixed
/// constant so EF migrations remain deterministic across generations.
public static class RbacSeedData
{
    public static readonly DateTime SeedTime =
        DateTime.SpecifyKind(new DateTime(2026, 1, 1), DateTimeKind.Utc);

    public static class PageIds
    {
        public static readonly Guid Dashboard      = Guid.Parse("10000000-0000-0000-0000-000000000001");
        public static readonly Guid Reports        = Guid.Parse("10000000-0000-0000-0000-000000000002");
        public static readonly Guid Users          = Guid.Parse("10000000-0000-0000-0000-000000000003");
        public static readonly Guid Organizations  = Guid.Parse("10000000-0000-0000-0000-000000000004");
        public static readonly Guid Subscriptions  = Guid.Parse("10000000-0000-0000-0000-000000000005");
        public static readonly Guid Payments       = Guid.Parse("10000000-0000-0000-0000-000000000006");
        public static readonly Guid Infographics   = Guid.Parse("10000000-0000-0000-0000-000000000007");
        public static readonly Guid AiJobs         = Guid.Parse("10000000-0000-0000-0000-000000000008");
        public static readonly Guid AuditLogs      = Guid.Parse("10000000-0000-0000-0000-000000000009");
        public static readonly Guid Rbac           = Guid.Parse("10000000-0000-0000-0000-00000000000a");
        public static readonly Guid Settings       = Guid.Parse("10000000-0000-0000-0000-00000000000b");
        public static readonly Guid Categories     = Guid.Parse("10000000-0000-0000-0000-00000000000c");
    }

    public static class RoleIds
    {
        // Existing org-scoped roles (pre-RBAC seeding) preserved for FK references.
        public static readonly Guid OrgAdminLegacy = Guid.Parse("00000000-0000-0000-0000-000000000001");
        public static readonly Guid Editor         = Guid.Parse("00000000-0000-0000-0000-000000000002");
        public static readonly Guid Viewer         = Guid.Parse("00000000-0000-0000-0000-000000000003");

        // New platform roles.
        public static readonly Guid SuperAdmin      = Guid.Parse("30000000-0000-0000-0000-000000000001");
        public static readonly Guid Admin           = Guid.Parse("30000000-0000-0000-0000-000000000002");
        public static readonly Guid ContentReviewer = Guid.Parse("30000000-0000-0000-0000-000000000003");
    }

    /// Bootstrap SuperAdmin so the staging environment has a working admin
    /// login the moment the migration runs. Password is fixed at seed time;
    /// the bcrypt hash below corresponds to the literal string
    /// `Taqreerk!Admin#2026`. Rotate via `/api/admin/auth/me/password` once
    /// staff management ships in Feature 1.
    public static class StaffUserIds
    {
        public static readonly Guid SuperAdmin = Guid.Parse("40000000-0000-0000-0000-000000000001");
    }

    private record PageSpec(Guid Id, string Key, string NameEn, string NameAr, int SortOrder);
    private record PermissionSpec(string Key, string NameEn, string NameAr);

    private static readonly PageSpec[] PageSpecs =
    [
        new(PageIds.Dashboard,     "dashboard",     "Dashboard",     "لوحة التحكم",    1),
        new(PageIds.Reports,       "reports",       "Reports",       "التقارير",         2),
        new(PageIds.Users,         "users",         "Users",         "المستخدمون",     3),
        new(PageIds.Organizations, "organizations", "Organizations", "المنظمات",        4),
        new(PageIds.Subscriptions, "subscriptions", "Subscriptions", "الاشتراكات",      5),
        new(PageIds.Payments,      "payments",      "Payments",      "المدفوعات",      6),
        new(PageIds.Infographics,  "infographics",  "Infographics",  "الإنفوغرافيك",   7),
        new(PageIds.AiJobs,        "ai_jobs",       "AI Jobs",       "مهام الذكاء",    8),
        new(PageIds.AuditLogs,     "audit_logs",    "Audit Logs",    "سجلات التدقيق", 9),
        new(PageIds.Rbac,          "rbac",          "Access Control","التحكم بالوصول",10),
        new(PageIds.Settings,      "settings",      "Settings",      "الإعدادات",      11),
        new(PageIds.Categories,    "categories",    "Categories",    "التصنيفات",      12),
    ];

    private static readonly PermissionSpec[] DefaultPermissions =
    [
        new("view",   "View",   "عرض"),
        new("create", "Create", "إنشاء"),
        new("edit",   "Edit",   "تعديل"),
        new("delete", "Delete", "حذف"),
    ];

    // Deterministic GUID: last 2 hex chars of pageId + 2-hex-char slot.
    // Page IDs end in 01..0b (11 pages), slots start at 01, so no collisions.
    public static Guid BuildPermissionId(Guid pageId, int slot)
    {
        var pageHex = pageId.ToString("N")[^2..];
        return Guid.Parse($"20000000-0000-0000-0000-00000000{pageHex}{slot:x2}");
    }

    public static IEnumerable<Page> Pages => PageSpecs.Select(s => new Page
    {
        Id = s.Id,
        Key = s.Key,
        NameEn = s.NameEn,
        NameAr = s.NameAr,
        SortOrder = s.SortOrder,
        IsSystem = true,
        CreatedAt = SeedTime,
    });

    public static IEnumerable<Permission> Permissions =>
        PageSpecs.SelectMany(page =>
            DefaultPermissions.Select((p, idx) => new Permission
            {
                Id = BuildPermissionId(page.Id, idx + 1),
                PageId = page.Id,
                Key = p.Key,
                NameEn = p.NameEn,
                NameAr = p.NameAr,
                IsSystem = true,
                CreatedAt = SeedTime,
            }));

    public static IEnumerable<Role> Roles =>
    [
        new Role { Id = RoleIds.OrgAdminLegacy,   Name = "admin",            Description = "Organization administrator",      Scope = RoleScope.Organization, IsSystem = true, CreatedAt = SeedTime },
        new Role { Id = RoleIds.Editor,           Name = "editor",           Description = "Can upload and edit reports",     Scope = RoleScope.Organization, IsSystem = true, CreatedAt = SeedTime },
        new Role { Id = RoleIds.Viewer,           Name = "viewer",           Description = "Read-only access",                Scope = RoleScope.Organization, IsSystem = true, CreatedAt = SeedTime },
        new Role { Id = RoleIds.SuperAdmin,       Name = "SuperAdmin",       Description = "Full platform access",             Scope = RoleScope.Platform,     IsSystem = true, CreatedAt = SeedTime },
        new Role { Id = RoleIds.Admin,            Name = "Admin",            Description = "Platform admin without RBAC mgmt", Scope = RoleScope.Platform,     IsSystem = true, CreatedAt = SeedTime },
        new Role { Id = RoleIds.ContentReviewer,  Name = "ContentReviewer",  Description = "Reviews submitted reports",        Scope = RoleScope.Platform,     IsSystem = true, CreatedAt = SeedTime },
    ];

    public static IEnumerable<RolePermission> RolePermissions
    {
        get
        {
            var allPermissions = Permissions.ToList();

            // SuperAdmin: everything.
            foreach (var p in allPermissions)
                yield return new RolePermission { RoleId = RoleIds.SuperAdmin, PermissionId = p.Id, CreatedAt = SeedTime };

            // Admin: everything except the RBAC and Categories pages —
            // those two are SuperAdmin-class platform configuration.
            foreach (var p in allPermissions
                         .Where(p => p.PageId != PageIds.Rbac
                                  && p.PageId != PageIds.Categories))
                yield return new RolePermission { RoleId = RoleIds.Admin, PermissionId = p.Id, CreatedAt = SeedTime };

            // ContentReviewer: view + edit on the Reports page only — they
            // don't get to delete reports, they don't see other admin pages.
            // The fine-grained workflow actions (claim/approve/reject) are
            // gated by role name, not by these page-level permissions.
            foreach (var p in allPermissions
                         .Where(p => p.PageId == PageIds.Reports
                                  && (p.Key == "view" || p.Key == "edit")))
                yield return new RolePermission { RoleId = RoleIds.ContentReviewer, PermissionId = p.Id, CreatedAt = SeedTime };
        }
    }
}
