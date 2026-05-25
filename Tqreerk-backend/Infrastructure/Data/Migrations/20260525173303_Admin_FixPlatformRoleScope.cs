using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <summary>
    /// The AddRbacSystem migration inserted SuperAdmin and Admin roles without
    /// specifying the Scope column, so PostgreSQL applied the column default of
    /// 1 (Organization) instead of the intended 0 (Platform).
    ///
    /// AdminAuthService.GetMyProfileAsync filters user roles by
    /// Scope == RoleScope.Platform (0), which returns nothing for these two
    /// roles — causing the /me endpoint to return empty roles and permissions,
    /// hiding all RBAC-gated menu items even for SuperAdmin.
    ///
    /// This migration sets Scope = 0 (Platform) for SuperAdmin and Admin.
    /// Idempotent — UPDATE WHERE is a no-op when the value is already correct.
    /// </summary>
    public partial class Admin_FixPlatformRoleScope : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE roles
                SET    "Scope" = 0   -- RoleScope.Platform
                WHERE  "Id" IN (
                    '30000000-0000-0000-0000-000000000001',   -- SuperAdmin
                    '30000000-0000-0000-0000-000000000002'    -- Admin
                )
                AND "Scope" <> 0;    -- no-op if already correct (staging)
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally left empty — reverting to the wrong Scope would
            // break the admin app. Rolling back this migration is a no-op.
        }
    }
}
