using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Feature5b_PointsAndMe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "point_transactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<int>(type: "integer", nullable: false),
                    BalanceAfter = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ActionType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_point_transactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_point_transactions_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_points",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Balance = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_points", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_points_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_point_transactions_user_created",
                table: "point_transactions",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_user_points_UserId",
                table: "user_points",
                column: "UserId",
                unique: true);

            // Backfill: every existing individual gets a 3000-point welcome
            // balance + the matching ledger row. Mirrors what
            // RegisterIndividualAsync does on signup so legacy users start
            // on equal footing. NOT EXISTS guard makes the migration safe
            // to re-run: anyone who already has a row keeps their balance.
            //
            // The two INSERTs are written as a single CTE so a user's
            // user_points and point_transactions rows always land in the
            // same transaction — no half-credited accounts if the
            // migration is interrupted.
            migrationBuilder.Sql("""
                WITH new_balances AS (
                    INSERT INTO user_points ("Id", "UserId", "Balance", "UpdatedAt", "CreatedAt")
                    SELECT
                        gen_random_uuid(),
                        u."Id",
                        3000,
                        now(),
                        now()
                    FROM users u
                    WHERE u."UserType" = 'individual'
                      AND u."DeletedAt" IS NULL
                      AND NOT EXISTS (
                          SELECT 1 FROM user_points p WHERE p."UserId" = u."Id"
                      )
                    RETURNING "UserId"
                )
                INSERT INTO point_transactions
                    ("Id", "UserId", "Amount", "BalanceAfter", "Reason",
                     "ActionType", "ResourceId", "CreatedAt")
                SELECT
                    gen_random_uuid(),
                    nb."UserId",
                    3000,
                    3000,
                    'welcome_credit',
                    NULL,
                    NULL,
                    now()
                FROM new_balances nb;
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "point_transactions");

            migrationBuilder.DropTable(
                name: "user_points");
        }
    }
}
