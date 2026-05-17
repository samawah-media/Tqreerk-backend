using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Taqreerk.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Feature_OrganizationCommercialDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "OrgCommercialRegisterExpiryDate",
                table: "PendingRegistrations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrgCommercialRegisterName",
                table: "PendingRegistrations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OrgEmployeeCount",
                table: "PendingRegistrations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrgLicenseDocumentUrl",
                table: "PendingRegistrations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrgTaxNumber",
                table: "PendingRegistrations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CommercialRegisterExpiryDate",
                table: "organization_profiles",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommercialRegisterName",
                table: "organization_profiles",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EmployeeCount",
                table: "organization_profiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxNumber",
                table: "organization_profiles",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OrgCommercialRegisterExpiryDate",
                table: "PendingRegistrations");

            migrationBuilder.DropColumn(
                name: "OrgCommercialRegisterName",
                table: "PendingRegistrations");

            migrationBuilder.DropColumn(
                name: "OrgEmployeeCount",
                table: "PendingRegistrations");

            migrationBuilder.DropColumn(
                name: "OrgLicenseDocumentUrl",
                table: "PendingRegistrations");

            migrationBuilder.DropColumn(
                name: "OrgTaxNumber",
                table: "PendingRegistrations");

            migrationBuilder.DropColumn(
                name: "CommercialRegisterExpiryDate",
                table: "organization_profiles");

            migrationBuilder.DropColumn(
                name: "CommercialRegisterName",
                table: "organization_profiles");

            migrationBuilder.DropColumn(
                name: "EmployeeCount",
                table: "organization_profiles");

            migrationBuilder.DropColumn(
                name: "TaxNumber",
                table: "organization_profiles");
        }
    }
}
