using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Adds the admin_action_logs table — cross-cutting audit trail for
    /// every action the platform-staff team performs (review decisions,
    /// org/user/plan changes, system-triggered claim releases). Distinct
    /// from `audit_logs` which is the generic event stream.
    /// </summary>
    public partial class Admin_ActionLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_action_logs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    AdminUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActionType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TargetEntityType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TargetEntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    BeforeState = table.Column<string>(type: "jsonb", nullable: true),
                    AfterState = table.Column<string>(type: "jsonb", nullable: true),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_action_logs", x => x.Id);
                    // Restrict on delete so removing a staff member doesn't
                    // erase their audit trail. Soft-deleting the user
                    // (the only path we expose) leaves the FK intact.
                    table.ForeignKey(
                        name: "FK_admin_action_logs_users_AdminUserId",
                        column: x => x.AdminUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Per-admin recent activity. Partial so system rows
            // (AdminUserId = NULL) don't bloat the index.
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_admin_action_logs_AdminUserId_CreatedAt_desc\" " +
                "ON admin_action_logs (\"AdminUserId\" ASC, \"CreatedAt\" DESC) " +
                "WHERE \"AdminUserId\" IS NOT NULL;");

            // Per-target history (e.g. all actions on a specific report).
            migrationBuilder.Sql(
                "CREATE INDEX \"IX_admin_action_logs_Target_CreatedAt_desc\" " +
                "ON admin_action_logs (\"TargetEntityType\" ASC, \"TargetEntityId\" ASC, \"CreatedAt\" DESC);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "admin_action_logs");
        }
    }
}
