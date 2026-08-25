using System.Globalization;
using System.Text.RegularExpressions;
using NodaTime;
using Takvim.Core.Recurrence;

namespace Takvim.Core.Localization;

/// <summary>Doğal dil metninden çıkarılan etkinlik taslağı.</summary>
public sealed record ParsedEvent(
    string Title,
    LocalDateTime Start,
    LocalDateTime End,
    bool IsAllDay,
    string? RecurrenceRule)
{
    /// <summary>Metinden tarih ya da saat çıkarılabildi mi. Çıkarılamadıysa arayüz varsayılanı gösterir.</summary>
    public bool RecognizedSchedule { get; init; }

    /// <summary>Kullanıcıya gösterilecek özet: "Perşembe, 14:00 – 15:00".</summary>
    public string Summary => IsAllDay
        ? $"{TurkishFormat.DayName(Start.Date)}, tüm gün"
        : $"{TurkishFormat.DayName(Start.Date)} {TurkishFormat.Date(Start.Date)}, " +
          $"{TurkishFormat.TimeRange(Start, End)}";
}

/// <summary>
/// Türkçe hızlı oluşturma ayrıştırıcısı.
/// <para>
/// "perşembe 14:00 Ahmet ile toplantı" gibi bir cümleden tarih, saat, süre ve
/// tekrar kuralını çıkarır; kalan metin başlık olur. Tanınan her ifade metinden
/// silinir, böylece başlıkta "perşembe 14:00" artığı kalmaz.
/// </para>
/// <para>
/// Türkçe'nin ekleri nedeniyle kalıplar sona gelen ekleri de yutar:
/// "perşembe<b>ye</b>", "15 Mart'<b>ta</b>", "saat 14:00'<b>te</b>".
/// </para>
/// </summary>
public static partial class TurkishEventParser
{
    /// <summary>Saat belirtilmemişse etkinliğin varsayılan başlangıcı.</summary>
    private static readonly LocalTime DefaultStartTime = new(9, 0);

    /// <summary>Süre belirtilmemişse varsayılan uzunluk.</summary>
    private static readonly Period DefaultDuration = Period.FromHours(1);

    private static readonly string[] Months =
        ["ocak", "şubat", "mart", "nisan", "mayıs", "haziran",
         "temmuz", "ağustos", "eylül", "ekim", "kasım", "aralık"];

    /// <summary>Pazartesi = IsoDayOfWeek.Monday.</summary>
    private static readonly (string Name, IsoDayOfWeek Day)[] Weekdays =
    [
        ("pazartesi", IsoDayOfWeek.Monday),
        ("salı", IsoDayOfWeek.Tuesday),
        ("çarşamba", IsoDayOfWeek.Wednesday),
        ("perşembe", IsoDayOfWeek.Thursday),
        ("cuma", IsoDayOfWeek.Friday),
        ("cumartesi", IsoDayOfWeek.Saturday),
        ("pazar", IsoDayOfWeek.Sunday),
    ];

    /// <summary>Metni çözümler. <paramref name="now"/> göreli ifadelerin dayanağıdır.</summary>
    public static ParsedEvent Parse(string? input, LocalDateTime now)
    {
        var text = " " + (input ?? string.Empty).Trim() + " ";
        var recognized = false;

        // Sıra önemlidir: tekrar kuralı gün adını tükettiği için önce o okunur,
        // aksi hâlde "her pazartesi" ifadesindeki gün adı tarih sanılır.
        var rrule = ExtractRecurrence(ref text, now, ref recognized);
        var isAllDay = ExtractAllDay(ref text, ref recognized);
        var date = ExtractDate(ref text, now, ref recognized);
        var (startTime, endTime) = ExtractTimes(ref text, ref recognized);
        var duration = ExtractDuration(ref text, ref recognized);

        var title = Cleanup(text);

        // Tarih verilmemişse: saat verildiyse bugün, verilmediyse bugün varsayılan saat.
        var day = date ?? now.Date;
        var start = isAllDay
            ? day.AtMidnight()
            : day + (startTime ?? (date is null && startTime is null ? NextSlot(now) : DefaultStartTime));

        LocalDateTime end;
        if (isAllDay)
            end = start.PlusDays(1);
        else if (endTime is { } explicitEnd)
        {
            end = day + explicitEnd;
            // 23:00-01:00 gibi gece aşan aralıklar ertesi güne taşar.
            if (end <= start) end = end.PlusDays(1);
        }
        else
            end = start + (duration ?? DefaultDuration);

        return new ParsedEvent(title, start, end, isAllDay, rrule) { RecognizedSchedule = recognized };
    }

    // ------------------------------------------------------------------
    // Tekrar
    // ------------------------------------------------------------------

    private static string? ExtractRecurrence(ref string text, LocalDateTime now, ref bool recognized)
    {
        // "hafta içi her gün" / "her hafta içi"
        if (TryConsume(ref text, WeekdaysOnlyPattern()))
        {
            recognized = true;
            return "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR";
        }

        // "iki haftada bir", "3 haftada bir"
        var interval = IntervalPattern().Match(text);
        if (interval.Success)
        {
            var count = ParseTurkishNumber(interval.Groups["sayi"].Value);
            var unit = interval.Groups["birim"].Value;
            text = text.Remove(interval.Index, interval.Length).Insert(interval.Index, " ");
            recognized = true;

            var freq = unit.StartsWith("gün", StringComparison.Ordinal) ? "DAILY"
                     : unit.StartsWith("hafta", StringComparison.Ordinal) ? "WEEKLY"
                     : unit.StartsWith("ay", StringComparison.Ordinal) ? "MONTHLY" : "YEARLY";

            return $"FREQ={freq};INTERVAL={count}";
        }

        // "her pazartesi", "her salı"
        foreach (var (name, day) in Weekdays)
        {
            if (!TryConsume(ref text, EveryWeekdayPattern(name))) continue;

            recognized = true;
            return $"FREQ=WEEKLY;BYDAY={IcalDay(day)}";
        }

        // "her gün", "her hafta", "her ay", "her yıl"
        var simple = SimpleEveryPattern().Match(text);
        if (simple.Success)
        {
            var unit = simple.Groups["birim"].Value;
            text = text.Remove(simple.Index, simple.Length).Insert(simple.Index, " ");
            recognized = true;

            return unit switch
            {
                "gün" => "FREQ=DAILY",
                "hafta" => $"FREQ=WEEKLY;BYDAY={IcalDay(now.Date.DayOfWeek)}",
                "ay" => $"FREQ=MONTHLY;BYMONTHDAY={now.Day}",
                _ => $"FREQ=YEARLY;BYMONTH={now.Month};BYMONTHDAY={now.Day}",
            };
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Tüm gün
    // ------------------------------------------------------------------

    private static bool ExtractAllDay(ref string text, ref bool recognized)
    {
        if (!TryConsume(ref text, AllDayPattern())) return false;

        recognized = true;
        return true;
    }

    // ------------------------------------------------------------------
    // Tarih
    // ------------------------------------------------------------------

    private static LocalDate? ExtractDate(ref string text, LocalDateTime now, ref bool recognized)
    {
        // "bugün", "yarın", "öbür gün", "dün"
        var relative = RelativeDayPattern().Match(text);
        if (relative.Success)
        {
            text = text.Remove(relative.Index, relative.Length).Insert(relative.Index, " ");
            recognized = true;

            // Kalıp büyük/küçük harf ayırmadan eşleşir, bu yüzden karşılaştırmadan
            // önce küçültülür: cümle başındaki "Yarın" da yarın demektir.
            // "öbür gün" ve "ertesi gün" grubun dışında kaldığı için boş düşer
            // ve varsayılan dala gider.
            return relative.Groups["gun"].Value.ToLower(TurkishFormat.Culture) switch
            {
                "bugün" or "bugun" => now.Date,
                "yarın" or "yarin" => now.Date.PlusDays(1),
                "dün" or "dun" => now.Date.PlusDays(-1),
                _ => now.Date.PlusDays(2),   // öbür gün / ertesi gün
            };
        }

        // "15 mart", "15 mart 2027"
        var named = NamedDatePattern().Match(text);
        if (named.Success)
        {
            var monthIndex = Array.IndexOf(Months, named.Groups["ay"].Value.ToLower(TurkishFormat.Culture));
            if (monthIndex >= 0)
            {
                var day = int.Parse(named.Groups["gun"].Value, CultureInfo.InvariantCulture);
                var year = named.Groups["yil"].Success
                    ? int.Parse(named.Groups["yil"].Value, CultureInfo.InvariantCulture)
                    : now.Year;

                if (day >= 1 && day <= CalendarSystem.Iso.GetDaysInMonth(year, monthIndex + 1))
                {
                    text = text.Remove(named.Index, named.Length).Insert(named.Index, " ");
                    recognized = true;

                    var candidate = new LocalDate(year, monthIndex + 1, day);
                    // Yıl yazılmamışsa ve tarih geçmişteyse gelecek yıl kastedilmiştir.
                    return !named.Groups["yil"].Success && candidate < now.Date
                        ? candidate.PlusYears(1)
                        : candidate;
                }
            }
        }

        // "15.03.2026", "15.03"
        var numeric = NumericDatePattern().Match(text);
        if (numeric.Success)
        {
            var day = int.Parse(numeric.Groups["gun"].Value, CultureInfo.InvariantCulture);
            var month = int.Parse(numeric.Groups["ay"].Value, CultureInfo.InvariantCulture);
            var year = numeric.Groups["yil"].Success
                ? NormalizeYear(int.Parse(numeric.Groups["yil"].Value, CultureInfo.InvariantCulture))
                : now.Year;

            if (month is >= 1 and <= 12 && day >= 1 && day <= CalendarSystem.Iso.GetDaysInMonth(year, month))
            {
                text = text.Remove(numeric.Index, numeric.Length).Insert(numeric.Index, " ");
                recognized = true;
                return new LocalDate(year, month, day);
            }
        }

        // "perşembe", "gelecek perşembe"
        foreach (var (name, weekday) in Weekdays)
        {
            var match = WeekdayPattern(name).Match(text);
            if (!match.Success) continue;

            text = text.Remove(match.Index, match.Length).Insert(match.Index, " ");
            recognized = true;

            var next = NextWeekday(now.Date, weekday);
            // "gelecek perşembe" bir hafta sonrasını işaret eder.
            return match.Groups["gelecek"].Success ? next.PlusDays(7) : next;
        }

        return null;
    }

    // ------------------------------------------------------------------
    // Saat
    // ------------------------------------------------------------------

    private static (LocalTime? Start, LocalTime? End) ExtractTimes(ref string text, ref bool recognized)
    {
        var range = TimeRangePattern().Match(text);
        if (range.Success)
        {
            var start = ReadTime(range.Groups["s1"].Value, range.Groups["d1"].Value);
            var end = ReadTime(range.Groups["s2"].Value, range.Groups["d2"].Value);

            if (start is not null && end is not null)
            {
                text = text.Remove(range.Index, range.Length).Insert(range.Index, " ");
                recognized = true;
                return (start, end);
            }
        }

        var single = SingleTimePattern().Match(text);
        if (single.Success)
        {
            var time = ReadTime(single.Groups["saat"].Value, single.Groups["dakika"].Value);
            if (time is not null)
            {
                text = text.Remove(single.Index, single.Length).Insert(single.Index, " ");
                recognized = true;
                return (time, null);
            }
        }

        return (null, null);
    }

    private static LocalTime? ReadTime(string hour, string minute)
    {
        if (!int.TryParse(hour, CultureInfo.InvariantCulture, out var h) || h is < 0 or > 23) return null;

        var m = 0;
        if (!string.IsNullOrEmpty(minute)
            && (!int.TryParse(minute, CultureInfo.InvariantCulture, out m) || m is < 0 or > 59))
        {
            return null;
        }

        return new LocalTime(h, m);
    }

    // ------------------------------------------------------------------
    // Süre
    // ------------------------------------------------------------------

    private static Period? ExtractDuration(ref string text, ref bool recognized)
    {
        var match = DurationPattern().Match(text);
        if (!match.Success) return null;

        var raw = match.Groups["sayi"].Value.Replace(',', '.');
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
        {
            amount = ParseTurkishNumber(match.Groups["sayi"].Value);
            if (amount == 0) return null;
        }

        text = text.Remove(match.Index, match.Length).Insert(match.Index, " ");
        recognized = true;

        var minutes = match.Groups["birim"].Value.StartsWith("saat", StringComparison.Ordinal)
            ? (int)Math.Round(amount * 60)
            : (int)Math.Round(amount);

        return Period.FromMinutes(Math.Max(minutes, 1));
    }

    // ------------------------------------------------------------------
    // Yardımcılar
    // ------------------------------------------------------------------

    private static bool TryConsume(ref string text, Regex pattern)
    {
        var match = pattern.Match(text);
        if (!match.Success) return false;

        text = text.Remove(match.Index, match.Length).Insert(match.Index, " ");
        return true;
    }

    /// <summary>Kalan metinden başlığı çıkarır: fazla boşluklar ve başta kalan bağlaçlar temizlenir.</summary>
    private static string Cleanup(string text)
    {
        var cleaned = WhitespacePattern().Replace(text, " ").Trim();
        cleaned = LeadingNoisePattern().Replace(cleaned, string.Empty).Trim();

        if (cleaned.Length == 0) return string.Empty;

        // İlk harf büyütülür; Türkçe kültürüyle yapılır ki "istanbul" -> "İstanbul" olsun.
        return string.Concat(cleaned[..1].ToUpper(TurkishFormat.Culture), cleaned[1..]);
    }

    /// <summary>Bugünden sonraki (bugün dahil) ilk <paramref name="target"/> günü.</summary>
    private static LocalDate NextWeekday(LocalDate from, IsoDayOfWeek target)
    {
        var delta = ((int)target - (int)from.DayOfWeek + 7) % 7;
        return from.PlusDays(delta);
    }

    /// <summary>Şu andan sonraki ilk yarım saatlik dilim.</summary>
    private static LocalTime NextSlot(LocalDateTime now)
    {
        var minute = now.Minute < 30 ? 30 : 0;
        var hour = now.Minute < 30 ? now.Hour : now.Hour + 1;
        return hour > 23 ? new LocalTime(23, 30) : new LocalTime(hour, minute);
    }

    private static int NormalizeYear(int year) => year < 100 ? 2000 + year : year;

    private static int ParseTurkishNumber(string word) => word.ToLower(TurkishFormat.Culture) switch
    {
        "bir" => 1,
        "iki" => 2,
        "üç" => 3,
        "dört" => 4,
        "beş" => 5,
        "altı" => 6,
        "yedi" => 7,
        "sekiz" => 8,
        "dokuz" => 9,
        "on" => 10,
        _ => int.TryParse(word, CultureInfo.InvariantCulture, out var value) ? value : 0,
    };

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

    // ------------------------------------------------------------------
    // Kalıplar
    // ------------------------------------------------------------------
    // Tümü büyük/küçük harf duyarsızdır. Sondaki "\w*" Türkçe ekleri yutar:
    // "perşembeye", "martta", "günlerde" gibi biçimler de eşleşir.

    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    /// <summary>Başlığın başında kalan bağlaç ve edatlar.</summary>
    [GeneratedRegex(@"^(?:ile|için|de|da|ve|saat|günü|tarihinde)\s+", Options)]
    private static partial Regex LeadingNoisePattern();

    [GeneratedRegex(@"\b(?:tüm|bütün)\s+gün\w*\b", Options)]
    private static partial Regex AllDayPattern();

    [GeneratedRegex(@"\bhafta\s*içi(?:\s+her\s+gün\w*)?\b", Options)]
    private static partial Regex WeekdaysOnlyPattern();

    [GeneratedRegex(@"\b(?<sayi>\d+|bir|iki|üç|dört|beş|altı|yedi|sekiz|dokuz|on)\s+(?<birim>gün|hafta|ay|yıl)\w*\s+bir\b", Options)]
    private static partial Regex IntervalPattern();

    [GeneratedRegex(@"\bher\s+(?<birim>gün|hafta|ay|yıl)\w*\b", Options)]
    private static partial Regex SimpleEveryPattern();

    // Aksansız yazımlar da kabul edilir: kullanıcı "yarin" yazabilir, ayrıca
    // kalıplar CultureInvariant olduğu için büyük harfli "YARIN"ın küçüğü
    // noktasız "yarin"dir ve dotlu "yarın" ile eşleşmez.
    [GeneratedRegex(
        @"\b(?<gun>bugün|bugun|yarın|yarin|dün|dun)\w*\b|\b(?:öbür|obur|ertesi)\s+gün\w*\b", Options)]
    private static partial Regex RelativeDayPattern();

    [GeneratedRegex(@"\b(?<gun>\d{1,2})\s+(?<ay>ocak|şubat|mart|nisan|mayıs|haziran|temmuz|ağustos|eylül|ekim|kasım|aralık)\w*(?:\s+(?<yil>\d{4}))?", Options)]
    private static partial Regex NamedDatePattern();

    [GeneratedRegex(@"\b(?<gun>\d{1,2})\.(?<ay>\d{1,2})(?:\.(?<yil>\d{2,4}))?\b", Options)]
    private static partial Regex NumericDatePattern();

    /// <summary>"14:00 - 15:30", "14-15", "14.00–15.30"</summary>
    [GeneratedRegex(@"\b(?<s1>\d{1,2})(?:[:.](?<d1>\d{2}))?\s*[-–—]\s*(?<s2>\d{1,2})(?:[:.](?<d2>\d{2}))?\b(?!\.\d)", Options)]
    private static partial Regex TimeRangePattern();

    /// <summary>"14:00", "saat 14", "14.30'da"</summary>
    [GeneratedRegex(@"\b(?:saat\s+)?(?<saat>\d{1,2})[:.](?<dakika>\d{2})\b|\bsaat\s+(?<saat>\d{1,2})\b", Options)]
    private static partial Regex SingleTimePattern();

    [GeneratedRegex(@"\b(?<sayi>\d+(?:[,.]\d+)?|bir|iki|üç|dört|beş|altı|yedi|sekiz|dokuz|on)\s+(?<birim>saat|dakika|dk)\w*\b(?!\s+bir\b)", Options)]
    private static partial Regex DurationPattern();

    private static Regex EveryWeekdayPattern(string day)
        => new($@"\bher\s+{Regex.Escape(day)}\w*\b", Options);

    private static Regex WeekdayPattern(string day)
        => new($@"\b(?:(?<gelecek>gelecek|önümüzdeki|haftaya)\s+)?{Regex.Escape(day)}\w*\b", Options);
}
