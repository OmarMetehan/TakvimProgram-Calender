using System.Globalization;
using System.Text;
using Ical.Net;
using Ical.Net.DataTypes;
using NodaTime;

namespace Takvim.Core.Recurrence;

/// <summary>Arayüzdeki tekrar açılır listesinin hazır seçenekleri.</summary>
public enum RecurrencePreset
{
    None,
    Daily,
    WeeklyOnStartDay,
    /// <summary>Hafta içi her gün (pazartesi-cuma).</summary>
    EveryWeekday,
    MonthlyOnDayOfMonth,
    /// <summary>Her ayın aynı sıradaki aynı günü, ör. "her ayın üçüncü salısı".</summary>
    MonthlyOnNthWeekday,
    Yearly,
    Custom,
}

/// <summary>
/// RRULE metinlerini kurar, doğrular ve Türkçe okunur hâle getirir.
/// Arayüz hiçbir yerde elle RRULE dizesi birleştirmez; tek kaynak burasıdır.
/// </summary>
public static class RecurrenceRuleBuilder
{
    private static readonly string[] DayNames =
        ["pazartesi", "salı", "çarşamba", "perşembe", "cuma", "cumartesi", "pazar"];

    private static readonly string[] MonthNames =
        ["Ocak", "Şubat", "Mart", "Nisan", "Mayıs", "Haziran",
         "Temmuz", "Ağustos", "Eylül", "Ekim", "Kasım", "Aralık"];

    private static readonly string[] Ordinals =
        ["", "birinci", "ikinci", "üçüncü", "dördüncü", "beşinci"];

    /// <summary>Hazır seçeneği, etkinliğin başlangıç tarihine göre RRULE metnine çevirir.</summary>
    public static string? FromPreset(RecurrencePreset preset, LocalDate start)
        => preset switch
        {
            RecurrencePreset.None or RecurrencePreset.Custom => null,
            RecurrencePreset.Daily => "FREQ=DAILY",
            RecurrencePreset.WeeklyOnStartDay => $"FREQ=WEEKLY;BYDAY={IcalDay(start.DayOfWeek)}",
            RecurrencePreset.EveryWeekday => "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR",
            RecurrencePreset.MonthlyOnDayOfMonth => $"FREQ=MONTHLY;BYMONTHDAY={start.Day}",
            RecurrencePreset.MonthlyOnNthWeekday => $"FREQ=MONTHLY;BYDAY={NthWeekdayToken(start)}",
            RecurrencePreset.Yearly => $"FREQ=YEARLY;BYMONTH={start.Month};BYMONTHDAY={start.Day}",
            _ => null,
        };

    /// <summary>Kuralın çözümlenebilir olup olmadığını sınar.</summary>
    public static bool TryParse(string? rrule, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(rrule)) return true;

        try
        {
            var pattern = new RecurrencePattern(rrule);
            if (pattern.Frequency == FrequencyType.Secondly || pattern.Frequency == FrequencyType.Minutely)
            {
                error = "Saniyelik ve dakikalık tekrar desteklenmiyor.";
                return false;
            }
            if (pattern.Interval < 1)
            {
                error = "Tekrar aralığı en az 1 olmalı.";
                return false;
            }
            if (pattern.Count is <= 0)
            {
                error = "Tekrar sayısı en az 1 olmalı.";
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            error = "Tekrar kuralı okunamadı: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Bir seriyi verilen tarihten itibaren keser. "Bu ve sonrakiler" düzenlemesinde,
    /// özgün seri bu kuralla sonlandırılıp kalanı yeni bir seri olarak açılır.
    /// </summary>
    public static string TruncateBefore(string rrule, Instant lastAllowedEndUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rrule);

        var pattern = new RecurrencePattern(rrule);
        // COUNT ve UNTIL aynı anda bulunamaz; kesme daima UNTIL ile ifade edilir.
        pattern.Count = null;

        var utc = lastAllowedEndUtc.ToDateTimeUtc();
        pattern.Until = new CalDateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, "UTC");

        return Normalize(pattern);
    }

    /// <summary>Kuralı standart sırayla yeniden yazar; karşılaştırma ve saklama için tekil biçim.</summary>
    public static string Normalize(string rrule) => Normalize(new RecurrencePattern(rrule));

    private static string Normalize(RecurrencePattern pattern)
    {
        var parts = new List<string> { "FREQ=" + pattern.Frequency.ToString().ToUpperInvariant() };

        if (pattern.Interval > 1) parts.Add("INTERVAL=" + pattern.Interval.ToString(CultureInfo.InvariantCulture));
        if (pattern.Count is { } count) parts.Add("COUNT=" + count.ToString(CultureInfo.InvariantCulture));
        if (pattern.Until is { } until) parts.Add("UNTIL=" + FormatUntil(until));
        if (pattern.ByMonth.Count > 0) parts.Add("BYMONTH=" + string.Join(',', pattern.ByMonth));
        if (pattern.ByMonthDay.Count > 0) parts.Add("BYMONTHDAY=" + string.Join(',', pattern.ByMonthDay));
        if (pattern.ByDay.Count > 0)
            parts.Add("BYDAY=" + string.Join(',', pattern.ByDay.Select(d =>
                (d.Offset is { } o && o != int.MinValue ? o.ToString(CultureInfo.InvariantCulture) : "") + IcalDay(d.DayOfWeek))));
        if (pattern.BySetPosition.Count > 0) parts.Add("BYSETPOS=" + string.Join(',', pattern.BySetPosition));

        return string.Join(';', parts);
    }

    /// <summary>
    /// UNTIL değerini RFC 5545 biçiminde yazar.
    /// <para>
    /// Bileşenler elle birleştirilir: <c>CalDateTime.ToString(format, provider)</c>
    /// çıktının sonuna zaman dilimi adını ekler ("...Z UTC") ve ortaya geçersiz bir
    /// kural çıkar. Bu sessiz bir hatadır; kural çözümlenemeyince seri süresiz sanılır.
    /// </para>
    /// </summary>
    private static string FormatUntil(CalDateTime until)
        => string.Create(CultureInfo.InvariantCulture,
            $"{until.Year:0000}{until.Month:00}{until.Day:00}T{until.Hour:00}{until.Minute:00}{until.Second:00}Z");

    // ------------------------------------------------------------------
    // Türkçe açıklama
    // ------------------------------------------------------------------

    /// <summary>
    /// Kuralı kullanıcıya gösterilecek Türkçe cümleye çevirir,
    /// ör. "İki haftada bir pazartesi ve çarşamba, 10 kez".
    /// </summary>
    public static string Describe(string? rrule, LocalDate start)
    {
        if (string.IsNullOrWhiteSpace(rrule)) return "Tekrarlamaz";

        RecurrencePattern pattern;
        try { pattern = new RecurrencePattern(rrule); }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { return "Özel tekrar"; }

        var text = new StringBuilder(pattern.Frequency switch
        {
            FrequencyType.Daily => DescribeDaily(pattern),
            FrequencyType.Weekly => DescribeWeekly(pattern, start),
            FrequencyType.Monthly => DescribeMonthly(pattern, start),
            FrequencyType.Yearly => DescribeYearly(pattern, start),
            _ => "Özel tekrar",
        });

        if (pattern.Count is { } count)
            text.Append(CultureInfo.CurrentCulture, $", {count} kez");
        else if (pattern.Until is { } until)
            text.Append(CultureInfo.CurrentCulture, $", {until.Day:00}.{until.Month:00}.{until.Year} tarihine kadar");

        return text.ToString();
    }

    private static string DescribeDaily(RecurrencePattern pattern)
        => pattern.Interval <= 1 ? "Her gün" : $"{Every(pattern.Interval)} günde bir";

    private static string DescribeWeekly(RecurrencePattern pattern, LocalDate start)
    {
        var days = pattern.ByDay.Count > 0
            ? pattern.ByDay.Select(d => DayNames[IsoIndex(d.DayOfWeek)]).ToList()
            : [DayNames[IsoIndex(start.DayOfWeek)]];

        // Hafta içi her gün için özel kısaltma: listelemek yerine tek ifade.
        if (days.Count == 5 && !days.Contains("cumartesi") && !days.Contains("pazar") && pattern.Interval <= 1)
            return "Hafta içi her gün";

        var joined = JoinTurkish(days);
        return pattern.Interval <= 1 ? $"Her hafta {joined}" : $"{Every(pattern.Interval)} haftada bir {joined}";
    }

    private static string DescribeMonthly(RecurrencePattern pattern, LocalDate start)
    {
        var prefix = pattern.Interval <= 1 ? "Her ay" : $"{Every(pattern.Interval)} ayda bir";

        if (pattern.ByDay.Count > 0)
        {
            var day = pattern.ByDay[0];
            var offset = day.Offset ?? 1;
            var nth = offset == -1 ? "son" : Ordinals[Math.Clamp(offset, 1, 5)];
            return $"{prefix} {nth} {DayNames[IsoIndex(day.DayOfWeek)]}";
        }

        var dayOfMonth = pattern.ByMonthDay.Count > 0 ? pattern.ByMonthDay[0] : start.Day;
        return dayOfMonth == -1 ? $"{prefix} son gün" : $"{prefix} ayın {dayOfMonth}'inde";
    }

    private static string DescribeYearly(RecurrencePattern pattern, LocalDate start)
    {
        var month = pattern.ByMonth.Count > 0 ? pattern.ByMonth[0] : start.Month;
        var day = pattern.ByMonthDay.Count > 0 ? pattern.ByMonthDay[0] : start.Day;
        var prefix = pattern.Interval <= 1 ? "Her yıl" : $"{Every(pattern.Interval)} yılda bir";
        return $"{prefix} {day} {MonthNames[month - 1]}";
    }

    private static string Every(int interval) => interval switch
    {
        2 => "İki",
        3 => "Üç",
        4 => "Dört",
        _ => interval.ToString(CultureInfo.CurrentCulture),
    };

    /// <summary>"pazartesi, çarşamba ve cuma" — son öğeden önce "ve" kullanır.</summary>
    private static string JoinTurkish(List<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " ve " + items[^1],
    };

    // ------------------------------------------------------------------

    /// <summary>NodaTime'ın ISO gün numarasını (pazartesi = 1) iCalendar kısaltmasına çevirir.</summary>
    private static string IcalDay(IsoDayOfWeek day) => day switch
    {
        IsoDayOfWeek.Monday => "MO",
        IsoDayOfWeek.Tuesday => "TU",
        IsoDayOfWeek.Wednesday => "WE",
        IsoDayOfWeek.Thursday => "TH",
        IsoDayOfWeek.Friday => "FR",
        IsoDayOfWeek.Saturday => "SA",
        _ => "SU",
    };

    private static string IcalDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "MO",
        DayOfWeek.Tuesday => "TU",
        DayOfWeek.Wednesday => "WE",
        DayOfWeek.Thursday => "TH",
        DayOfWeek.Friday => "FR",
        DayOfWeek.Saturday => "SA",
        _ => "SU",
    };

    /// <summary>Pazartesi = 0 olacak şekilde dizin. Türkiye'de hafta pazartesi başlar.</summary>
    private static int IsoIndex(DayOfWeek day) => ((int)day + 6) % 7;

    private static int IsoIndex(IsoDayOfWeek day) => (int)day - 1;

    /// <summary>Tarihin ayın kaçıncı o günü olduğunu bulur, ör. 17.03.2026 için "3TU".</summary>
    private static string NthWeekdayToken(LocalDate date)
    {
        var nth = (date.Day - 1) / 7 + 1;

        // Ayın son haftasındaysa "-1" kullanmak daha doğrudur: 5. hafta her ayda yoktur.
        var daysInMonth = CalendarSystem.Iso.GetDaysInMonth(date.Year, date.Month);
        if (date.Day + 7 > daysInMonth) return "-1" + IcalDay(date.DayOfWeek);

        return nth.ToString(CultureInfo.InvariantCulture) + IcalDay(date.DayOfWeek);
    }
}
