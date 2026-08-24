using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Takvim.Data.Migrations
{
    /// <inheritdoc />
    public partial class YanitSaati : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RespondedForStartLocal",
                table: "Attendees",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RespondedForStartLocal",
                table: "Attendees");
        }
    }
}
