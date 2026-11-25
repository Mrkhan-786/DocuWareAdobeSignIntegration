using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocuWareAdobeSignIntegration.Migrations
{
    /// <inheritdoc />
    public partial class AddSignedPdfFileCabinet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DocuWareArchiveError",
                table: "AdobeAgreementWorkflows",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DocuWareArchiveStatus",
                table: "AdobeAgreementWorkflows",
                type: "TEXT",
                maxLength: 50,
                nullable: false,
                defaultValue: "Not saved");

            migrationBuilder.AddColumn<string>(
                name: "SignedPdfPath",
                table: "AdobeAgreementWorkflows",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SignedPdfSavedAtUtc",
                table: "AdobeAgreementWorkflows",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SignedPdfSizeBytes",
                table: "AdobeAgreementWorkflows",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdobeAgreementWorkflows_UserId_SignedPdfSavedAtUtc",
                table: "AdobeAgreementWorkflows",
                columns: new[] { "UserId", "SignedPdfSavedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AdobeAgreementWorkflows_UserId_SignedPdfSavedAtUtc",
                table: "AdobeAgreementWorkflows");

            migrationBuilder.DropColumn(
                name: "DocuWareArchiveError",
                table: "AdobeAgreementWorkflows");

            migrationBuilder.DropColumn(
                name: "DocuWareArchiveStatus",
                table: "AdobeAgreementWorkflows");

            migrationBuilder.DropColumn(
                name: "SignedPdfPath",
                table: "AdobeAgreementWorkflows");

            migrationBuilder.DropColumn(
                name: "SignedPdfSavedAtUtc",
                table: "AdobeAgreementWorkflows");

            migrationBuilder.DropColumn(
                name: "SignedPdfSizeBytes",
                table: "AdobeAgreementWorkflows");
        }
    }
}
