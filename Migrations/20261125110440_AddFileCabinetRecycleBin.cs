using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocuWareAdobeSignIntegration.Migrations
{
    /// <inheritdoc />
    public partial class AddFileCabinetRecycleBin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeletedAtUtc",
                table: "AdobeAgreementWorkflows",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "AdobeAgreementWorkflows",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAtUtc",
                table: "AdobeAgreementWorkflows");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "AdobeAgreementWorkflows");
        }
    }
}
