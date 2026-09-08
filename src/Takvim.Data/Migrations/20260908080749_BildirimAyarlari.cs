using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Takvim.Data.Migrations
{
    /// <summary>
    /// Bildirim tercihleri: hatırlatıcı anahtarı ve sessiz saatler.
    /// </summary>
    /// <remarks>
    /// Var olan satırların değerleri elle verilmiştir. EF'in ürettiği
    /// varsayılanlar burada yanlış olurdu: <c>RemindersEnabled</c> için
    /// <c>false</c>, güncelleyen herkesin hatırlatıcılarını sessizce kapatırdı;
    /// saat sütunlarının boş dizesi ise <c>LocalTime</c> olarak
    /// çözümlenemez ve kullanıcı okunurken hata verirdi.
    /// </remarks>
    public partial class BildirimAyarlari : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "QuietHoursEnabled",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "QuietHoursEnd",
                table: "Users",
                type: "TEXT",
                nullable: false,
                defaultValue: "08:00:00");

            migrationBuilder.AddColumn<string>(
                name: "QuietHoursStart",
                table: "Users",
                type: "TEXT",
                nullable: false,
                defaultValue: "22:00:00");

            migrationBuilder.AddColumn<bool>(
                name: "QuietOnDaysOff",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RemindersEnabled",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QuietHoursEnabled",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "QuietHoursEnd",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "QuietHoursStart",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "QuietOnDaysOff",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "RemindersEnabled",
                table: "Users");
        }
    }
}
