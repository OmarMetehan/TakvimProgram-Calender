using NodaTime;
using Takvim.Core.Localization;

namespace Takvim.Server.State;

/// <summary>Izgara görünümleri. Adres çubuğunda kısa kodlarıyla saklanır.</summary>
public enum CalendarViewKind
{
    Day,
    Week,
    WorkWeek,
    /// <summary>Kullanıcının seçtiği gün sayısı (2-7).</summary>
    Days,
    Month,
    Year,
    /// <summary>Zamanlama listesi: kronolojik akış.</summary>
    Schedule,
}

/// <summary>Izgaradaki satır yoğunluğu.</summary>
public enum GridDensity
{
    Comfortable,
    Compact,
}

/// <summary>Birden çok takvimin nasıl çizileceği.</summary>
public enum CalendarDisplayMode
{
    /// <summary>Hepsi aynı ızgarada üst üste.</summary>
    Overlay,
    /// <summary>Her takvim kendi sütununda yan yana.</summary>
    Columns,
}

/// <summary>
/// Görünümün adres çubuğunda saklanan durumu. Paylaşılan bir bağlantı,
/// açan kişide birebir aynı görünümü açar.
/// </summary>
public sealed record CalendarViewState
{
    public CalendarViewKind Kind { get; init; } = CalendarViewKind.Week;

    /// <summary>Görünümün dayandığı tarih. Hafta ve ay görünümlerinde bu tarihi içeren dönem gösterilir.</summary>
    public LocalDate Anchor { get; init; }

    /// <summary>Yalnızca <see cref="CalendarViewKind.Days"/> için: gösterilecek gün sayısı.</summary>
    public int DayCount { get; init; } = 3;

    public GridDensity Density { get; init; } = GridDensity.Comfortable;
    public CalendarDisplayMode DisplayMode { get; init; } = CalendarDisplayMode.Overlay;
    public bool HideWeekends { get; init; }

    public string? SearchTerm { get; init; }
    public IReadOnlyList<Guid> HiddenCalendarIds { get; init; } = [];
    public IReadOnlyList<Guid> CategoryFilter { get; init; } = [];

    // ------------------------------------------------------------------
    // Görünen tarih aralığı
    // ------------------------------------------------------------------

    /// <summary>Izgarada çizilen ilk gün.</summary>
    public LocalDate RangeStart => Kind switch
    {
        CalendarViewKind.Day => Anchor,
        CalendarViewKind.Week or CalendarViewKind.WorkWeek => StartOfWeek(Anchor),
        CalendarViewKind.Days => Anchor,
        CalendarViewKind.Month => StartOfWeek(new LocalDate(Anchor.Year, Anchor.Month, 1)),
        CalendarViewKind.Year => new LocalDate(Anchor.Year, 1, 1),
        _ => Anchor,
    };

    /// <summary>Izgarada çizilen son günün ertesi günü (hariç).</summary>
    public LocalDate RangeEnd => Kind switch
    {
        CalendarViewKind.Day => Anchor.PlusDays(1),
        CalendarViewKind.Week => StartOfWeek(Anchor).PlusDays(7),
        CalendarViewKind.WorkWeek => StartOfWeek(Anchor).PlusDays(5),
        CalendarViewKind.Days => Anchor.PlusDays(Math.Clamp(DayCount, 2, 7)),
        // Ay görünümü tam haftalarla çizilir; son hafta sonraki aya taşabilir.
        CalendarViewKind.Month => StartOfWeek(new LocalDate(Anchor.Year, Anchor.Month, 1)).PlusWeeks(WeeksInMonthGrid),
        CalendarViewKind.Year => new LocalDate(Anchor.Year + 1, 1, 1),
        _ => Anchor.PlusDays(30),
    };

    /// <summary>Ay ızgarasının kaç hafta süreceği. Şubat ayı pazartesi başlıyorsa 4 hafta yeter.</summary>
    public int WeeksInMonthGrid
    {
        get
        {
            var first = new LocalDate(Anchor.Year, Anchor.Month, 1);
            var gridStart = StartOfWeek(first);
            var lastDay = first.PlusMonths(1).PlusDays(-1);
            return (Period.DaysBetween(gridStart, lastDay) / 7) + 1;
        }
    }

    /// <summary>Izgarada gösterilecek günler; hafta sonu gizleme uygulanmış hâliyle.</summary>
    public IReadOnlyList<LocalDate> VisibleDays
    {
        get
        {
            var days = new List<LocalDate>();
            for (var day = RangeStart; day < RangeEnd; day = day.PlusDays(1))
            {
                if (HideWeekends && IsWeekend(day)) continue;
                days.Add(day);
            }
            return days;
        }
    }

    /// <summary>Üst çubukta gösterilen başlık.</summary>
    public string Title => Kind switch
    {
        CalendarViewKind.Day => TurkishFormat.LongDateWithDay(Anchor),
        CalendarViewKind.Month => TurkishFormat.MonthAndYear(Anchor),
        CalendarViewKind.Year => Anchor.Year.ToString(TurkishFormat.Culture),
        CalendarViewKind.Schedule => $"{TurkishFormat.LongDate(RangeStart)} sonrası",
        _ => TurkishFormat.DateRange(RangeStart, RangeEnd.PlusDays(-1)),
    };

    // ------------------------------------------------------------------
    // Gezinme
    // ------------------------------------------------------------------

    /// <summary>Bir dönem ileri veya geri gider.</summary>
    public CalendarViewState Shift(int steps) => this with
    {
        Anchor = Kind switch
        {
            CalendarViewKind.Day => Anchor.PlusDays(steps),
            CalendarViewKind.Week or CalendarViewKind.WorkWeek => Anchor.PlusWeeks(steps),
            CalendarViewKind.Days => Anchor.PlusDays(steps * Math.Clamp(DayCount, 2, 7)),
            CalendarViewKind.Month => Anchor.PlusMonths(steps),
            CalendarViewKind.Year => Anchor.PlusYears(steps),
            _ => Anchor.PlusDays(steps * 30),
        },
    };

    public CalendarViewState GoTo(LocalDate date) => this with { Anchor = date };

    public CalendarViewState WithKind(CalendarViewKind kind) => this with { Kind = kind };

    public bool Contains(LocalDate date) => date >= RangeStart && date < RangeEnd;

    // ------------------------------------------------------------------
    // Adres çubuğu
    // ------------------------------------------------------------------

    /// <summary>Durumu bağlantıya çevirir. Yalnızca varsayılandan sapan alanlar yazılır.</summary>
    public string ToQueryString()
    {
        var parts = new List<string>
        {
            "g=" + KindCode(Kind),
            "t=" + Anchor.ToString("uuuu-MM-dd", TurkishFormat.Culture),
        };

        if (Kind == CalendarViewKind.Days) parts.Add("n=" + DayCount.ToString(TurkishFormat.Culture));
        if (Density == GridDensity.Compact) parts.Add("yog=kompakt");
        if (DisplayMode == CalendarDisplayMode.Columns) parts.Add("mod=sutun");
        if (HideWeekends) parts.Add("hs=1");
        if (!string.IsNullOrWhiteSpace(SearchTerm)) parts.Add("q=" + Uri.EscapeDataString(SearchTerm));
        if (HiddenCalendarIds.Count > 0) parts.Add("gizli=" + string.Join(",", HiddenCalendarIds.Select(i => i.ToString("N"))));
        if (CategoryFilter.Count > 0) parts.Add("kat=" + string.Join(",", CategoryFilter.Select(i => i.ToString("N"))));

        return "?" + string.Join("&", parts);
    }

    /// <summary>Bağlantıdaki sorgu değerlerinden durumu kurar. Bozuk değerler varsayılana düşer.</summary>
    public static CalendarViewState FromQuery(IReadOnlyDictionary<string, string?> query, LocalDate today)
    {
        var kind = query.TryGetValue("g", out var g) ? ParseKind(g) : CalendarViewKind.Week;
        var anchor = query.TryGetValue("t", out var t) && TurkishFormat.TryParseDate(t, out var parsed)
            ? parsed
            : today;

        return new CalendarViewState
        {
            Kind = kind,
            Anchor = anchor,
            DayCount = query.TryGetValue("n", out var n) && int.TryParse(n, out var days)
                ? Math.Clamp(days, 2, 7) : 3,
            Density = query.TryGetValue("yog", out var d) && d == "kompakt"
                ? GridDensity.Compact : GridDensity.Comfortable,
            DisplayMode = query.TryGetValue("mod", out var m) && m == "sutun"
                ? CalendarDisplayMode.Columns : CalendarDisplayMode.Overlay,
            HideWeekends = query.TryGetValue("hs", out var hs) && hs == "1",
            SearchTerm = query.TryGetValue("q", out var q) && !string.IsNullOrWhiteSpace(q) ? q : null,
            HiddenCalendarIds = ParseIds(query, "gizli"),
            CategoryFilter = ParseIds(query, "kat"),
        };
    }

    private static IReadOnlyList<Guid> ParseIds(IReadOnlyDictionary<string, string?> query, string key)
    {
        if (!query.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return [];

        return [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => Guid.TryParse(part, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)];
    }

    public static string KindCode(CalendarViewKind kind) => kind switch
    {
        CalendarViewKind.Day => "gun",
        CalendarViewKind.Week => "hafta",
        CalendarViewKind.WorkWeek => "ishaftasi",
        CalendarViewKind.Days => "ngun",
        CalendarViewKind.Month => "ay",
        CalendarViewKind.Year => "yil",
        _ => "zamanlama",
    };

    private static CalendarViewKind ParseKind(string? code) => code switch
    {
        "gun" => CalendarViewKind.Day,
        "ishaftasi" => CalendarViewKind.WorkWeek,
        "ngun" => CalendarViewKind.Days,
        "ay" => CalendarViewKind.Month,
        "yil" => CalendarViewKind.Year,
        "zamanlama" => CalendarViewKind.Schedule,
        _ => CalendarViewKind.Week,
    };

    /// <summary>Haftanın ilk günü pazartesidir.</summary>
    public static LocalDate StartOfWeek(LocalDate date) => date.PlusDays(-((int)date.DayOfWeek - 1));

    public static bool IsWeekend(LocalDate date)
        => date.DayOfWeek is IsoDayOfWeek.Saturday or IsoDayOfWeek.Sunday;
}
