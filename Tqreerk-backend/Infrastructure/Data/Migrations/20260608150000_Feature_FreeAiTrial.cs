using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// Free individual tier: one smart-summary view + one AI chat message
    /// per month for demos. AiAccessLevel moves from "none" to "trial".
    /// </remarks>
    public partial class Feature_FreeAiTrial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE plans SET
                    "AiSummarizeLimit" = 1,
                    "AiAccessLevel" = 'trial'
                WHERE "Id" = '70000000-0000-0000-0000-000000000001'::uuid;
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE plans SET
                    "AiSummarizeLimit" = 0,
                    "AiAccessLevel" = 'none'
                WHERE "Id" = '70000000-0000-0000-0000-000000000001'::uuid;
            """);
        }
    }
}
