using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Feature_PendingRegistrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PendingRegistrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    FullName = table.Column<string>(type: "text", nullable: false),
                    Phone = table.Column<string>(type: "text", nullable: true),
                    UserType = table.Column<string>(type: "text", nullable: false),
                    JobTitle = table.Column<string>(type: "text", nullable: true),
                    InterestField = table.Column<string>(type: "text", nullable: true),
                    CountryId = table.Column<Guid>(type: "uuid", nullable: true),
                    PreferredLanguage = table.Column<string>(type: "text", nullable: false),
                    OrgNameAr = table.Column<string>(type: "text", nullable: true),
                    OrgNameEn = table.Column<string>(type: "text", nullable: true),
                    OrgType = table.Column<int>(type: "integer", nullable: true),
                    OrgCity = table.Column<string>(type: "text", nullable: true),
                    OrgWebsiteUrl = table.Column<string>(type: "text", nullable: true),
                    OrgSectorScope = table.Column<string>(type: "text", nullable: true),
                    OrgCommercialRegisterNo = table.Column<string>(type: "text", nullable: true),
                    OrgIssuesReports = table.Column<bool>(type: "boolean", nullable: false),
                    OrgAnnualReportsCount = table.Column<int>(type: "integer", nullable: true),
                    OrgWantsToPublish = table.Column<bool>(type: "boolean", nullable: false),
                    OrgInterestedInSubscription = table.Column<bool>(type: "boolean", nullable: false),
                    OrgContactPersonName = table.Column<string>(type: "text", nullable: true),
                    OrgContactPersonTitle = table.Column<string>(type: "text", nullable: true),
                    OrgContactEmail = table.Column<string>(type: "text", nullable: true),
                    OrgPoliciesAccepted = table.Column<bool>(type: "boolean", nullable: false),
                    OtpHash = table.Column<string>(type: "text", nullable: false),
                    OtpExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingRegistrations", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingRegistrations");
        }
    }
}
