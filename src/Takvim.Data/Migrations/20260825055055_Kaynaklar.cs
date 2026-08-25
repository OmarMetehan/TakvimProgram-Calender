using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Takvim.Data.Migrations
{
    /// <inheritdoc />
    public partial class Kaynaklar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Resources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CalendarId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Capacity = table.Column<int>(type: "INTEGER", nullable: true),
                    Location = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Features = table.Column<int>(type: "INTEGER", nullable: false),
                    Policy = table.Column<int>(type: "INTEGER", nullable: false),
                    ApproverUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Resources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Resources_Calendars_CalendarId",
                        column: x => x.CalendarId,
                        principalTable: "Calendars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ResourceBookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    HoldEventId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ResponseNote = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    RequestedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestedAt = table.Column<string>(type: "TEXT", nullable: false),
                    RespondedAt = table.Column<string>(type: "TEXT", nullable: true),
                    StartUtc = table.Column<string>(type: "TEXT", nullable: false),
                    EndUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceBookings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ResourceBookings_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ResourceBookings_Resources_ResourceId",
                        column: x => x.ResourceId,
                        principalTable: "Resources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResourceBookings_EventId",
                table: "ResourceBookings",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceBookings_ResourceId_EventId",
                table: "ResourceBookings",
                columns: new[] { "ResourceId", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ResourceBookings_ResourceId_StartUtc_EndUtc",
                table: "ResourceBookings",
                columns: new[] { "ResourceId", "StartUtc", "EndUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Resources_CalendarId",
                table: "Resources",
                column: "CalendarId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Resources_IsActive",
                table: "Resources",
                column: "IsActive");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResourceBookings");

            migrationBuilder.DropTable(
                name: "Resources");
        }
    }
}
