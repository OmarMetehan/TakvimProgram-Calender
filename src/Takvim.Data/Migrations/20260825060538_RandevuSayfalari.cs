using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Takvim.Data.Migrations
{
    /// <inheritdoc />
    public partial class RandevuSayfalari : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppointmentSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    DurationMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    BufferMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    MinimumNoticeHours = table.Column<int>(type: "INTEGER", nullable: false),
                    MaximumAdvanceDays = table.Column<int>(type: "INTEGER", nullable: false),
                    MaximumPerDay = table.Column<int>(type: "INTEGER", nullable: true),
                    CalendarId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LocationText = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    OnlineMeetingProvider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppointmentSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppointmentSchedules_Calendars_CalendarId",
                        column: x => x.CalendarId,
                        principalTable: "Calendars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppointmentSchedules_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Appointments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BookedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    GuestName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    GuestEmail = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    StartUtc = table.Column<string>(type: "TEXT", nullable: false),
                    EndUtc = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    CancelledAt = table.Column<string>(type: "TEXT", nullable: true),
                    CancellationReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Appointments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Appointments_AppointmentSchedules_ScheduleId",
                        column: x => x.ScheduleId,
                        principalTable: "AppointmentSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Appointments_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppointmentWindows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DayOfWeek = table.Column<int>(type: "INTEGER", nullable: false),
                    Start = table.Column<string>(type: "TEXT", nullable: false),
                    End = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppointmentWindows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppointmentWindows_AppointmentSchedules_ScheduleId",
                        column: x => x.ScheduleId,
                        principalTable: "AppointmentSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_EventId",
                table: "Appointments",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_ScheduleId_StartUtc",
                table: "Appointments",
                columns: new[] { "ScheduleId", "StartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentSchedules_CalendarId",
                table: "AppointmentSchedules",
                column: "CalendarId");

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentSchedules_OwnerUserId",
                table: "AppointmentSchedules",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentSchedules_Slug",
                table: "AppointmentSchedules",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppointmentWindows_ScheduleId",
                table: "AppointmentWindows",
                column: "ScheduleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Appointments");

            migrationBuilder.DropTable(
                name: "AppointmentWindows");

            migrationBuilder.DropTable(
                name: "AppointmentSchedules");
        }
    }
}
