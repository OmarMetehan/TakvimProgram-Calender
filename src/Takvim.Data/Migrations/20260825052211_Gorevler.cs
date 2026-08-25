using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Takvim.Data.Migrations
{
    /// <inheritdoc />
    public partial class Gorevler : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskLists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Color = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskLists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskLists_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskListId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParentTaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: true),
                    DueDate = table.Column<string>(type: "TEXT", nullable: true),
                    DueTime = table.Column<string>(type: "TEXT", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<string>(type: "TEXT", nullable: true),
                    RecurrenceRule = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    RecurrenceStartDate = table.Column<string>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    ScheduledEventId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    DeletedAt = table.Column<string>(type: "TEXT", nullable: true),
                    SearchText = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tasks_Events_ScheduledEventId",
                        column: x => x.ScheduledEventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Tasks_TaskLists_TaskListId",
                        column: x => x.TaskListId,
                        principalTable: "TaskLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Tasks_Tasks_ParentTaskId",
                        column: x => x.ParentTaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskLists_OwnerUserId",
                table: "TaskLists",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_DueDate",
                table: "Tasks",
                column: "DueDate");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_ParentTaskId",
                table: "Tasks",
                column: "ParentTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_ScheduledEventId",
                table: "Tasks",
                column: "ScheduledEventId");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_TaskListId_DeletedAt",
                table: "Tasks",
                columns: new[] { "TaskListId", "DeletedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Tasks");

            migrationBuilder.DropTable(
                name: "TaskLists");
        }
    }
}
