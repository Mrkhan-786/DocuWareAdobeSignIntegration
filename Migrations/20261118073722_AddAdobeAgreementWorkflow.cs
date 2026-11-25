using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DocuWareAdobeSignIntegration.Migrations
{
    /// <inheritdoc />
    public partial class AddAdobeAgreementWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdobeAgreementWorkflows",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AgreementId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    OriginalDocumentId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    FileCabinetId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SectionId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    SignerName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    SignerEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ArchivedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ArchivedDocumentId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    FailureMessage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdobeAgreementWorkflows", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdobeAgreementWorkflows_AgreementId",
                table: "AdobeAgreementWorkflows",
                column: "AgreementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdobeAgreementWorkflows_UserId_SentAtUtc",
                table: "AdobeAgreementWorkflows",
                columns: new[] { "UserId", "SentAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdobeAgreementWorkflows");
        }
    }
}
