using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Takvim.Data.Migrations
{
    /// <inheritdoc />
    public partial class IlkSema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChangeLog",
                columns: table => new
                {
                    SyncToken = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CalendarId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Operation = table.Column<int>(type: "INTEGER", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OnBehalfOfUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ChangedAt = table.Column<string>(type: "TEXT", nullable: false),
                    BeforeJson = table.Column<string>(type: "TEXT", nullable: true),
                    AfterJson = table.Column<string>(type: "TEXT", nullable: true),
                    Summary = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangeLog", x => x.SyncToken);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Locale = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    FirstDayOfWeek = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Calendars",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Color = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    IsVisible = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsReadOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DefaultReminderMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    DefaultAllDayReminderMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    SourceUrl = table.Column<string>(type: "TEXT", nullable: true),
                    LastSyncedAt = table.Column<string>(type: "TEXT", nullable: true),
                    SyncToken = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    DeletedAt = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Calendars", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Calendars_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Categories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Color = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    IsPrivate = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Categories_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CalendarId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Uid = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    StartLocal = table.Column<string>(type: "TEXT", nullable: false),
                    EndLocal = table.Column<string>(type: "TEXT", nullable: false),
                    StartTimeZoneId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    EndTimeZoneId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    IsAllDay = table.Column<bool>(type: "INTEGER", nullable: false),
                    StartUtc = table.Column<string>(type: "TEXT", nullable: false),
                    EndUtc = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    DescriptionHtml = table.Column<string>(type: "TEXT", nullable: true),
                    LocationText = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    OnlineMeetingUrl = table.Column<string>(type: "TEXT", nullable: true),
                    OnlineMeetingProvider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Color = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    SearchText = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    Availability = table.Column<int>(type: "INTEGER", nullable: false),
                    Visibility = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    IsForwardable = table.Column<bool>(type: "INTEGER", nullable: false),
                    OrganizerUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RecurrenceRule = table.Column<string>(type: "TEXT", nullable: true),
                    ExDates = table.Column<string>(type: "TEXT", nullable: true),
                    RDates = table.Column<string>(type: "TEXT", nullable: true),
                    SeriesEndUtc = table.Column<string>(type: "TEXT", nullable: true),
                    RecurrenceId = table.Column<string>(type: "TEXT", nullable: true),
                    SeriesId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    ETag = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LastModifiedUtc = table.Column<string>(type: "TEXT", nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", nullable: true),
                    RemoteId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    DeletedAt = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Events_Calendars_CalendarId",
                        column: x => x.CalendarId,
                        principalTable: "Calendars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Events_Events_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Events_Users_OrganizerUserId",
                        column: x => x.OrganizerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "EventCategories",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CategoryId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventCategories", x => new { x.EventId, x.CategoryId });
                    table.ForeignKey(
                        name: "FK_EventCategories_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EventCategories_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Reminders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MinutesBefore = table.Column<int>(type: "INTEGER", nullable: false),
                    Channel = table.Column<int>(type: "INTEGER", nullable: false),
                    SnoozedUntil = table.Column<string>(type: "TEXT", nullable: true),
                    LastFiredForOccurrenceAt = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reminders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Reminders_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Calendars_DeletedAt",
                table: "Calendars",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Calendars_OwnerUserId_SortOrder",
                table: "Calendars",
                columns: new[] { "OwnerUserId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_Categories_OwnerUserId_Name",
                table: "Categories",
                columns: new[] { "OwnerUserId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChangeLog_CalendarId_SyncToken",
                table: "ChangeLog",
                columns: new[] { "CalendarId", "SyncToken" });

            migrationBuilder.CreateIndex(
                name: "IX_ChangeLog_EntityType_EntityId",
                table: "ChangeLog",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChangeLog_OperationId",
                table: "ChangeLog",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_EventCategories_CategoryId",
                table: "EventCategories",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Events_CalendarId_StartUtc_EndUtc",
                table: "Events",
                columns: new[] { "CalendarId", "StartUtc", "EndUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_CalendarId_Uid_RecurrenceId",
                table: "Events",
                columns: new[] { "CalendarId", "Uid", "RecurrenceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_DeletedAt",
                table: "Events",
                column: "DeletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Events_OrganizerUserId",
                table: "Events",
                column: "OrganizerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Events_SearchText",
                table: "Events",
                column: "SearchText");

            migrationBuilder.CreateIndex(
                name: "IX_Events_SeriesEndUtc",
                table: "Events",
                column: "SeriesEndUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Events_SeriesId_RecurrenceId",
                table: "Events",
                columns: new[] { "SeriesId", "RecurrenceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Reminders_EventId",
                table: "Reminders",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChangeLog");

            migrationBuilder.DropTable(
                name: "EventCategories");

            migrationBuilder.DropTable(
                name: "Reminders");

            migrationBuilder.DropTable(
                name: "Categories");

            migrationBuilder.DropTable(
                name: "Events");

            migrationBuilder.DropTable(
                name: "Calendars");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
