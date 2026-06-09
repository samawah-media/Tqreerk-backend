using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class UpdateSupportEmail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE system_settings SET "Value" = 'taqrerk@samawah1.sa'
                WHERE "Key" IN ('support_email', 'email.support_reply_to');
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE system_settings SET "Value" = 'support@taqreerk.com'
                WHERE "Key" IN ('support_email', 'email.support_reply_to');
            """);
        }
    }
}
