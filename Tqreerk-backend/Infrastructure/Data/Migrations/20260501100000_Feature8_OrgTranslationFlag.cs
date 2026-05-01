using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds Organization.TranslationEnabled — a per-org gate for the manual
    /// translation feature. Default is false so existing orgs land in the
    /// "translate disabled" state until staff explicitly opts them in from
    /// the admin app's Organizations page.
    /// </summary>
    public partial class Feature8_OrgTranslationFlag : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TranslationEnabled",
                table: "organizations",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TranslationEnabled",
                table: "organizations");
        }
    }
}
