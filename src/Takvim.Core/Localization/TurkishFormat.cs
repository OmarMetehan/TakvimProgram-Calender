using System.Globalization;
using NodaTime;

namespace Takvim.Core.Localization;

/// <summary>
/// Türkçe tarih ve saat biçimleri. Varsayılanlar: 24 saat, GG.AA.YYYY,
/// haftanın ilk günü pazartesi. Arayüzde hiçbir yerde elle biçimlendirme yapılmaz.
/// </summary>
public static class TurkishFormat
{
    public static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("tr-TR");

    public static readonly string[] MonthNames =
        ["Ocak", "Şubat", "Mart", "Nisan", "Mayıs", "Haziran",
         "Temmuz", "Ağustos", "Eylül", "Ekim", "Kasım", "Aralık"];

    /// <summary>Pazartesiden başlar.</summary>
    public static readonly string[] DayNames =
        ["Pazartesi", "Salı", "Çarşamba", "Perşembe", "Cuma", "Cumartesi", "Pazar"];

    /// <summary>Izgara başlıklarında kullanılan iki harfli kısaltmalar.</summary>
    public static readonly string[] DayAbbreviations =
        ["Pzt", "Sal", "Çar", "Per", "Cum", "Cmt", "Paz"];

    // ------------------------------------------------------------------
    // Tekil değerler
    // ------------------------------------------------------------------

    /// <summary>02.03.2026</summary>
    public static string Date(LocalDate date) => $"{date.Day:00}.{date.Month:00}.{date.Year}";

    /// <summary>14:30</summary>
    public static string Time(LocalTime time) => $"{time.Hour:00}:{time.Minute:00}";

    public static string Time(LocalDateTime value) => Time(value.TimeOfDay);

    /// <summary>02.03.2026 14:30</summary>
    public static string DateTime(LocalDateTime value) => $"{Date(value.Date)} {Time(value.TimeOfDay)}";

    /// <summary>2 Mart 2026</summary>
    public static string LongDate(LocalDate date) => $"{date.Day} {MonthNames[date.Month - 1]} {date.Year}";

    /// <summary>2 Mart 2026 Pazartesi</summary>
    public static string LongDateWithDay(LocalDate date) => $"{LongDate(date)} {DayName(date)}";

    /// <summary>2 Mart — yıl olmadan</summary>
    public static string DayAndMonth(LocalDate date) => $"{date.Day} {MonthNames[date.Month - 1]}";

    public static string DayName(LocalDate date) => DayNames[DayIndex(date)];

    /// <summary>Haftanın gününün adı; tarih olmadan, ör. haftalık pencerelerde.</summary>
    public static string DayName(IsoDayOfWeek day) => DayNames[(int)day - 1];

    public static string DayAbbreviation(LocalDate date) => DayAbbreviations[DayIndex(date)];

    /// <summary>Mart 2026</summary>
    public static string MonthAndYear(LocalDate date) => $"{MonthNames[date.Month - 1]} {date.Year}";

    /// <summary>Pazartesi = 0. Türkiye'de hafta pazartesi başlar.</summary>
    public static int DayIndex(LocalDate date) => (int)date.DayOfWeek - 1;

    // ------------------------------------------------------------------
    // Aralıklar
    // ------------------------------------------------------------------

    /// <summary>09:00 – 10:30</summary>
    public static string TimeRange(LocalDateTime start, LocalDateTime end)
        => $"{Time(start)} – {Time(end)}";

    /// <summary>
    /// Görünüm başlığı için tarih aralığı. Ortak olan ay ve yıl tekrarlanmaz:
    /// "2 – 8 Mart 2026", "26 Şubat – 4 Mart 2026", "28 Aralık 2026 – 3 Ocak 2027".
    /// </summary>
    public static string DateRange(LocalDate from, LocalDate to)
    {
        if (from == to) return LongDate(from);

        if (from.Year != to.Year)
            return $"{LongDate(from)} – {LongDate(to)}";

        if (from.Month != to.Month)
            return $"{DayAndMonth(from)} – {LongDate(to)}";

        return $"{from.Day} – {to.Day} {MonthNames[to.Month - 1]} {to.Year}";
    }

    /// <summary>
    /// Etkinlik kartında gösterilen süre etiketi:
    /// "09:00", "09:00 – 10:30", "Tüm gün", "2 gün".
    /// </summary>
    public static string OccurrenceTime(LocalDateTime start, LocalDateTime end, bool isAllDay)
    {
        if (!isAllDay) return start.TimeOfDay == end.TimeOfDay ? Time(start) : TimeRange(start, end);

        var days = Period.DaysBetween(start.Date, end.Date);
        return days <= 1 ? "Tüm gün" : $"{days} gün";
    }

    // ------------------------------------------------------------------
    // Göreli ifadeler
    // ------------------------------------------------------------------

    /// <summary>"Bugün", "Yarın", "Dün" ya da tam tarih.</summary>
    public static string Relative(LocalDate date, LocalDate today) => Period.DaysBetween(today, date) switch
    {
        0 => "Bugün",
        1 => "Yarın",
        -1 => "Dün",
        _ => LongDate(date),
    };

    /// <summary>Süreyi okunur biçime çevirir: "45 dakika", "1 saat", "1 sa 30 dk".</summary>
    public static string Duration(NodaTime.Duration duration)
    {
        var totalMinutes = (int)Math.Round(duration.TotalMinutes);
        if (totalMinutes < 60) return $"{totalMinutes} dakika";

        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;

        if (minutes == 0) return $"{hours} saat";
        return $"{hours} sa {minutes} dk";
    }

    /// <summary>Hatırlatıcı süresini okunur biçime çevirir: "10 dakika önce", "1 gün önce".</summary>
    public static string ReminderOffset(int minutesBefore) => minutesBefore switch
    {
        0 => "Etkinlik anında",
        < 0 => $"{-minutesBefore} dakika sonra",
        < 60 => $"{minutesBefore} dakika önce",
        < 60 * 24 when minutesBefore % 60 == 0 => $"{minutesBefore / 60} saat önce",
        < 60 * 24 => $"{minutesBefore / 60} sa {minutesBefore % 60} dk önce",
        < 60 * 24 * 7 when minutesBefore % (60 * 24) == 0 => $"{minutesBefore / (60 * 24)} gün önce",
        _ when minutesBefore % (60 * 24 * 7) == 0 => $"{minutesBefore / (60 * 24 * 7)} hafta önce",
        _ => $"{minutesBefore / (60 * 24)} gün önce",
    };

    // ------------------------------------------------------------------
    // Ayrıştırma
    // ------------------------------------------------------------------

    /// <summary>GG.AA.YYYY biçimindeki metni tarihe çevirir.</summary>
    public static bool TryParseDate(string? text, out LocalDate date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (!System.DateTime.TryParseExact(text.Trim(), ["dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd"],
                Culture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        date = LocalDate.FromDateTime(parsed);
        return true;
    }

    /// <summary>SS:DD biçimindeki metni saate çevirir.</summary>
    public static bool TryParseTime(string? text, out LocalTime time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (!System.DateTime.TryParseExact(text.Trim(), ["HH:mm", "H:mm", "HHmm", "HH.mm"],
                Culture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        time = LocalTime.FromHourMinuteSecondTick(parsed.Hour, parsed.Minute, 0, 0);
        return true;
    }

    /// <summary>ISO haftası numarası. Türkiye ISO 8601 kullanır.</summary>
    public static int WeekNumber(LocalDate date)
        => System.Globalization.ISOWeek.GetWeekOfYear(date.ToDateTimeUnspecified());
}
