using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class UpdateFeaturedSectionLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE system_settings SET "Value" = '4'
                WHERE "Key" IN ('featured.max_homepage_hero', 'featured.max_carousel');
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE system_settings SET "Value" = '3'
                WHERE "Key" = 'featured.max_homepage_hero';
                UPDATE system_settings SET "Value" = '10'
                WHERE "Key" = 'featured.max_carousel';
            """);
        }
    }
}
