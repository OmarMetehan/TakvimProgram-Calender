using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Takvim.Data.Migrations
{
    /// <inheritdoc />
    public partial class KatilimcilarVePaylasim : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Attendees",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    Response = table.Column<int>(type: "INTEGER", nullable: false),
                    ResponseComment = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    RespondedAt = table.Column<string>(type: "TEXT", nullable: true),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    CanEdit = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanInviteOthers = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanSeeGuestList = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProposedStartLocal = table.Column<string>(type: "TEXT", nullable: true),
                    ProposedEndLocal = table.Column<string>(type: "TEXT", nullable: true),
                    ProposalNote = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    NotifiedSequence = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attendees", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Attendees_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Attendees_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "CalendarShares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CalendarId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GranteeUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDelegate = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanSeePrivateItems = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendarShares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CalendarShares_Calendars_CalendarId",
                        column: x => x.CalendarId,
                        principalTable: "Calendars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CalendarShares_Users_GranteeUserId",
                        column: x => x.GranteeUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Attendees_EventId_Email",
                table: "Attendees",
                columns: new[] { "EventId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Attendees_UserId_Response",
                table: "Attendees",
                columns: new[] { "UserId", "Response" });

            migrationBuilder.CreateIndex(
                name: "IX_CalendarShares_CalendarId_GranteeUserId",
                table: "CalendarShares",
                columns: new[] { "CalendarId", "GranteeUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalendarShares_GranteeUserId",
                table: "CalendarShares",
                column: "GranteeUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Attendees");

            migrationBuilder.DropTable(
                name: "CalendarShares");
        }
    }
}
