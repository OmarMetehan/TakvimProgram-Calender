using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Takvim.Data.Migrations
{
    /// <inheritdoc />
    public partial class PostaKutulari : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MailAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<int>(type: "INTEGER", nullable: false),
                    EmailAddress = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    ProtectedRefreshToken = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ScannedThrough = table.Column<string>(type: "TEXT", nullable: true),
                    LastScanAt = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    LookbackDays = table.Column<int>(type: "INTEGER", nullable: false),
                    ConnectedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailAccounts_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EventProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MailAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageId = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    From = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    Snippet = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ReceivedAt = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    StartLocal = table.Column<string>(type: "TEXT", nullable: false),
                    EndLocal = table.Column<string>(type: "TEXT", nullable: false),
                    IsAllDay = table.Column<bool>(type: "INTEGER", nullable: false),
                    LocationText = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    OnlineMeetingUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    RecognizedSchedule = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedEventId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventProposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventProposals_MailAccounts_MailAccountId",
                        column: x => x.MailAccountId,
                        principalTable: "MailAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventProposals_MailAccountId_MessageId",
                table: "EventProposals",
                columns: new[] { "MailAccountId", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventProposals_Status",
                table: "EventProposals",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_MailAccounts_UserId_Provider_EmailAddress",
                table: "MailAccounts",
                columns: new[] { "UserId", "Provider", "EmailAddress" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventProposals");

            migrationBuilder.DropTable(
                name: "MailAccounts");
        }
    }
}
