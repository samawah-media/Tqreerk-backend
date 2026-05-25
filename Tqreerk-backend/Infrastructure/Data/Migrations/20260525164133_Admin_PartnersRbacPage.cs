using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Backfill migration that ensures the RBAC pages, permissions, and role
    /// grants for <c>categories</c>, <c>featured</c>, and <c>partners</c> are
    /// present in every environment.
    ///
    /// Background
    /// ──────────
    /// • <c>categories</c> was seeded by Feature6_Categories but its Admin
    ///   role grant was omitted (SuperAdmin-only at the time).
    /// • <c>featured</c> was seeded by Feature7_FeaturedReports for both roles.
    /// • <c>partners</c> was never added to RbacSeedData at all.
    ///
    /// In staging all of the above are present because every migration ran in
    /// order.  In production some of those migrations were skipped, so
    /// categories/featured/partners pages and their role_permissions are
    /// missing, which causes the frontend to hide those menu items even for
    /// SuperAdmin.
    ///
    /// Every INSERT uses ON CONFLICT DO NOTHING, so re-running on a DB that
    /// is already fully seeded is a harmless no-op.
    /// </summary>
    public partial class Admin_PartnersRbacPage : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                -- ── 1. Pages ──────────────────────────────────────────────────────────
                INSERT INTO pages (
                    "Id", "Key", "NameEn", "NameAr", "SortOrder", "IsSystem", "CreatedAt"
                ) VALUES
                    ('10000000-0000-0000-0000-00000000000c',
                     'categories', 'Categories', 'التصنيفات',   12, TRUE, '2026-01-01 00:00:00+00'),
                    ('10000000-0000-0000-0000-00000000000d',
                     'featured',   'Featured',   'المحتوى البارز', 13, TRUE, '2026-01-01 00:00:00+00'),
                    ('10000000-0000-0000-0000-00000000000e',
                     'partners',   'Partners',   'الشركاء',      14, TRUE, '2026-01-01 00:00:00+00')
                ON CONFLICT ("Id") DO NOTHING;

                -- ── 2. Permissions (4 per page) ───────────────────────────────────────
                INSERT INTO permissions (
                    "Id", "PageId", "Key", "NameEn", "NameAr", "IsSystem", "CreatedAt"
                ) VALUES
                    -- categories (0c)
                    ('20000000-0000-0000-0000-000000000c01',
                     '10000000-0000-0000-0000-00000000000c',
                     'view',   'View',   'عرض',   TRUE, '2026-01-01 00:00:00+00'),
                    ('20000000-0000-0000-0000-000000000c02',
                     '10000000-0000-0000-0000-00000000000c',
                     'create', 'Create', 'إنشاء', TRUE, '2026-01-01 00:00:00+00'),
                    ('20000000-0000-0000-0000-000000000c03',
                     '10000000-0000-0000-0000-00000000000c',
                     'edit',   'Edit',   'تعديل', TRUE, '2026-01-01 00:00:00+00'),
                    ('20000000-0000-0000-0000-000000000c04',
                     '10000000-0000-0000-0000-00000000000c',
                     'delete', 'Delete', 'حذف',   TRUE, '2026-01-01 00:00:00+00'),
                    -- featured (0d)
                    ('20000000-0000-0000-0000-000000000d01',
                     '10000000-0000-0000-0000-00000000000d',
                     'view',   'View',   'عرض',   TRUE, '2026-01-01 00:00:00+00'),
                    ('20000000-0000-0000-0000-000000000d02',
                     '10000000-0000-0000-0000-00000000000d',
                     'create', 'Create', 'إنشاء', TRUE, '2026-01-01 00:00:00+00'),
                    ('20000000-0000-0000-0000-000000000d03',
                     '10000000-0000-0000-0000-00000000000d',
                     'edit',   'Edit',   'تعديل', TRUE, '2026-01-01 00:00:00+00'),
                    ('20000000-0000-0000-0000-000000000d04',
                     '10000000-0000-0000-0000-00000000000d',
                     'delete', 'Delete', 'حذف',   TRUE, '2026-01-01 00:00:00+00'),
                    -- partners (0e)
                    ('20000000-0000-0000-0000-000000000e01',
                     '10000000-0000-0000-0000-00000000000e',
                     'view',   'View',   'عرض',   TRUE, '2026-01-01 00:00:00+00'),
                    ('20000000-0000-0000-0000-000000000e02',
                     '10000000-0000-0000-0000-00000000000e',
                     'create', 'Create', 'إنشاء', TRUE, '2026-01-01 00:00:00+00'),
                    ('20000000-0000-0000-0000-000000000e03',
                     '10000000-0000-0000-0000-00000000000e',
                     'edit',   'Edit',   'تعديل', TRUE, '2026-01-01 00:00:00+00'),
                    ('20000000-0000-0000-0000-000000000e04',
                     '10000000-0000-0000-0000-00000000000e',
                     'delete', 'Delete', 'حذف',   TRUE, '2026-01-01 00:00:00+00')
                ON CONFLICT ("Id") DO NOTHING;

                -- ── 3. SuperAdmin → ALL permissions ───────────────────────────────────
                -- Selects every permission in the DB so any already-granted ones are
                -- silently skipped and any missing ones are inserted.
                INSERT INTO role_permissions ("RoleId", "PermissionId", "CreatedAt")
                SELECT
                    '30000000-0000-0000-0000-000000000001',
                    p."Id",
                    '2026-01-01 00:00:00+00'
                FROM permissions p
                ON CONFLICT DO NOTHING;

                -- ── 4. Admin → all permissions EXCEPT rbac + categories ────────────────
                -- rbac   (0a): SuperAdmin-only by design.
                -- categories (0c): SuperAdmin-only by design.
                INSERT INTO role_permissions ("RoleId", "PermissionId", "CreatedAt")
                SELECT
                    '30000000-0000-0000-0000-000000000002',
                    p."Id",
                    '2026-01-01 00:00:00+00'
                FROM permissions p
                JOIN pages pg ON pg."Id" = p."PageId"
                WHERE pg."Key" NOT IN ('rbac', 'categories')
                ON CONFLICT DO NOTHING;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove only the partners page (new to this migration).
            // categories and featured are owned by Feature6_Categories /
            // Feature7_FeaturedReports — rolling those back is their job.
            migrationBuilder.Sql("""
                DELETE FROM role_permissions
                WHERE "PermissionId" IN (
                    SELECT pm."Id"
                    FROM permissions pm
                    JOIN pages pg ON pg."Id" = pm."PageId"
                    WHERE pg."Key" = 'partners'
                );

                DELETE FROM permissions
                WHERE "PageId" = '10000000-0000-0000-0000-00000000000e';

                DELETE FROM pages
                WHERE "Id" = '10000000-0000-0000-0000-00000000000e';
                """);
        }
    }
}
