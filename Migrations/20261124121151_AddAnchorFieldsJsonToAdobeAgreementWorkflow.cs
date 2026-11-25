using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocuWareAdobeSignIntegration.Migrations
{
    /// <inheritdoc />
    public partial class AddAnchorFieldsJsonToAdobeAgreementWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AnchorFieldsJson",
                table: "AdobeAgreementWorkflows",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "RecipientsJson",
                table: "AdobeAgreementWorkflows",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "SigningOrderMode",
                table: "AdobeAgreementWorkflows",
                type: "TEXT",
                maxLength: 30,
                nullable: false,
                defaultValue: "Sequential");

            migrationBuilder.CreateTable(
                name: "DashboardUserStates",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    DashboardClearedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DashboardUserStates", x => x.UserId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DashboardUserStates");

            migrationBuilder.DropColumn(
                name: "AnchorFieldsJson",
                table: "AdobeAgreementWorkflows");

            migrationBuilder.DropColumn(
                name: "RecipientsJson",
                table: "AdobeAgreementWorkflows");

            migrationBuilder.DropColumn(
                name: "SigningOrderMode",
                table: "AdobeAgreementWorkflows");
        }
    }
}
