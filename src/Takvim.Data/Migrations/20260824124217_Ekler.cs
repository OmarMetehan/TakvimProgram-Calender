using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Takvim.Data.Migrations
{
    /// <inheritdoc />
    public partial class Ekler : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Attachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    StorageName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    UploadedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Attachments_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_EventId",
                table: "Attachments",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_StorageName",
                table: "Attachments",
                column: "StorageName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Attachments");
        }
    }
}
