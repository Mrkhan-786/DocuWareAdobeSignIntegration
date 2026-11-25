using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocuWareAdobeSignIntegration.Migrations
{
    /// <inheritdoc />
    public partial class AddDocuWareTokenStorageAndArchiveTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ArchiveAttempts",
                table: "AdobeAgreementWorkflows",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "ArchiveInProgress",
                table: "AdobeAgreementWorkflows",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastArchiveAttemptUtc",
                table: "AdobeAgreementWorkflows",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DocuWareUserTokens",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    EncryptedAccessToken = table.Column<string>(type: "TEXT", nullable: false),
                    EncryptedRefreshToken = table.Column<string>(type: "TEXT", nullable: false),
                    AccessTokenExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocuWareUserTokens", x => x.UserId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdobeAgreementWorkflows_ArchiveInProgress_LastArchiveAttemptUtc",
                table: "AdobeAgreementWorkflows",
                columns: new[] { "ArchiveInProgress", "LastArchiveAttemptUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AdobeAgreementWorkflows_Status_SentAtUtc",
                table: "AdobeAgreementWorkflows",
                columns: new[] { "Status", "SentAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocuWareUserTokens");

            migrationBuilder.DropIndex(
                name: "IX_AdobeAgreementWorkflows_ArchiveInProgress_LastArchiveAttemptUtc",
                table: "AdobeAgreementWorkflows");

            migrationBuilder.DropIndex(
                name: "IX_AdobeAgreementWorkflows_Status_SentAtUtc",
                table: "AdobeAgreementWorkflows");

            migrationBuilder.DropColumn(
                name: "ArchiveAttempts",
                table: "AdobeAgreementWorkflows");

            migrationBuilder.DropColumn(
                name: "ArchiveInProgress",
                table: "AdobeAgreementWorkflows");

            migrationBuilder.DropColumn(
                name: "LastArchiveAttemptUtc",
                table: "AdobeAgreementWorkflows");
        }
    }
}
