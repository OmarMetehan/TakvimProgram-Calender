using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using NodaTime;

namespace Takvim.Core.Localization;

/// <summary>Tatilin niteliği. Yarım gün mesai ve "tam gün tatil" ayrımı buradan yapılır.</summary>
public enum HolidayKind
{
    /// <summary>Tam gün resmi tatil.</summary>
    Official,
    /// <summary>Arefe: yarım gün mesai. Öğleden sonra tatildir.</summary>
    HalfDayEve,
    /// <summary>Resmi tatil olmayan anma günü.</summary>
    Observance,
}

/// <summary>Takvimde gösterilen tek bir tatil günü.</summary>
public sealed record Holiday(
    LocalDate Date,
    string Name,
    HolidayKind Kind,
    /// <summary>Yarım gün mesailerde işin bittiği saat. Tam gün tatillerde null.</summary>
    LocalTime? WorkEndsAt = null,
    /// <summary>Tarih Diyanet takviminden teyit edilmemişse true; arayüz "tahmini" gösterir.</summary>
    bool IsEstimated = false)
{
    public bool IsDayOff => Kind == HolidayKind.Official;
}

/// <summary>
/// Türkiye resmi tatil takvimi.
/// <para>
/// Sabit tarihli milli bayramlar koddan üretilir. Dini bayramlar hicri takvime
/// bağlı olduğu için hesaplanmaz, <c>dini-gunler.json</c> tablosundan okunur:
/// Diyanet'in ilan ettiği tarih ile hesaplanan hicri takvim bazı yıllar bir gün
/// ayrışır ve mesai Diyanet'e göre işler.
/// </para>
/// </summary>
public sealed class TurkishHolidays
{
    /// <summary>Arefe günlerinde mesainin bittiği saat (2429 sayılı kanun).</summary>
    public static readonly LocalTime EveWorkEndsAt = new(13, 0);

    private readonly FrozenDictionary<int, ReligiousYear> _religious;
    private readonly Dictionary<int, IReadOnlyList<Holiday>> _cache = [];
    private readonly Lock _cacheLock = new();

    public TurkishHolidays(string? jsonOverride = null)
    {
        var json = jsonOverride ?? LoadEmbeddedJson();
        var table = JsonSerializer.Deserialize<ReligiousTable>(json, JsonOptions)
                    ?? throw new InvalidOperationException("Dini gün tablosu okunamadı.");

        _religious = table.Yillar.ToFrozenDictionary(y => y.Yil);
    }

    /// <summary>Tablodaki en son yıl. Bu yıldan sonrası için dini bayram bilinmez.</summary>
    public int LastKnownReligiousYear => _religious.Count == 0 ? 0 : _religious.Keys.Max();

    /// <summary>Bir yılın tüm tatillerini tarih sırasıyla verir.</summary>
    public IReadOnlyList<Holiday> ForYear(int year)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(year, out var cached)) return cached;
        }

        var holidays = Build(year);

        lock (_cacheLock)
        {
            _cache[year] = holidays;
        }
        return holidays;
    }

    /// <summary>Verilen tarih aralığındaki tatiller.</summary>
    public IEnumerable<Holiday> InRange(LocalDate fromInclusive, LocalDate toInclusive)
    {
        for (var year = fromInclusive.Year; year <= toInclusive.Year; year++)
        {
            foreach (var holiday in ForYear(year))
            {
                if (holiday.Date >= fromInclusive && holiday.Date <= toInclusive)
                    yield return holiday;
            }
        }
    }

    /// <summary>Tarih tam gün resmi tatil mi. Tekrarlayan etkinliklerin tatil atlaması bunu kullanır.</summary>
    public bool IsDayOff(LocalDate date)
        => ForYear(date.Year).Any(h => h.Date == date && h.IsDayOff);

    /// <summary>Tarih yarım gün mesai ise mesainin bitiş saati, değilse null.</summary>
    public LocalTime? HalfDayEndsAt(LocalDate date)
        => ForYear(date.Year).FirstOrDefault(h => h.Date == date && h.Kind == HolidayKind.HalfDayEve)?.WorkEndsAt;

    public IEnumerable<Holiday> On(LocalDate date) => ForYear(date.Year).Where(h => h.Date == date);

    // ------------------------------------------------------------------

    private List<Holiday> Build(int year)
    {
        var holidays = new List<Holiday>
        {
            new(new LocalDate(year, 1, 1), "Yılbaşı", HolidayKind.Official),
            new(new LocalDate(year, 4, 23), "Ulusal Egemenlik ve Çocuk Bayramı", HolidayKind.Official),
            new(new LocalDate(year, 5, 1), "Emek ve Dayanışma Günü", HolidayKind.Official),
            new(new LocalDate(year, 5, 19), "Atatürk'ü Anma, Gençlik ve Spor Bayramı", HolidayKind.Official),
            new(new LocalDate(year, 7, 15), "Demokrasi ve Millî Birlik Günü", HolidayKind.Official),
            new(new LocalDate(year, 8, 30), "Zafer Bayramı", HolidayKind.Official),

            // Cumhuriyet Bayramı 29 Ekim'dir; 28 Ekim saat 13:00'ten itibaren tatil başlar.
            new(new LocalDate(year, 10, 28), "Cumhuriyet Bayramı arefesi", HolidayKind.HalfDayEve, EveWorkEndsAt),
            new(new LocalDate(year, 10, 29), "Cumhuriyet Bayramı", HolidayKind.Official),
        };

        AddReligious(holidays, year);

        holidays.Sort((a, b) => a.Date.CompareTo(b.Date));
        return holidays;
    }

    private void AddReligious(List<Holiday> holidays, int year)
    {
        // Bir bayram yılın ilk günlerine denk geldiğinde arefesi bir önceki yıla düşer;
        // bu yüzden komşu yılların kayıtları da taranır.
        foreach (var candidateYear in new[] { year - 1, year, year + 1 })
        {
            if (!_religious.TryGetValue(candidateYear, out var entry)) continue;

            AddFeast(holidays, year, entry.RamazanBayrami, "Ramazan Bayramı", days: 3, entry.Dogrulandi);
            AddFeast(holidays, year, entry.KurbanBayrami, "Kurban Bayramı", days: 4, entry.Dogrulandi);
        }
    }

    private static void AddFeast(List<Holiday> holidays, int year, string firstDayIso, string name, int days, bool verified)
    {
        var firstDay = LocalDate.FromDateTime(
            DateTime.ParseExact(firstDayIso, "yyyy-MM-dd", CultureInfo.InvariantCulture));

        // Arefe: bayramın bir önceki günü, 13:00'ten sonrası tatil.
        var eve = firstDay.PlusDays(-1);
        if (eve.Year == year)
            holidays.Add(new Holiday(eve, name + " arefesi", HolidayKind.HalfDayEve, EveWorkEndsAt, !verified));

        for (var offset = 0; offset < days; offset++)
        {
            var date = firstDay.PlusDays(offset);
            if (date.Year != year) continue;

            var label = $"{name} {offset + 1}. gün";
            holidays.Add(new Holiday(date, label, HolidayKind.Official, null, !verified));
        }
    }

    private static string LoadEmbeddedJson()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string resourceName = "Takvim.Core.Localization.dini-gunler.json";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Gömülü kaynak bulunamadı: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private sealed record ReligiousTable
    {
        [JsonPropertyName("yillar")]
        public List<ReligiousYear> Yillar { get; init; } = [];
    }

    private sealed record ReligiousYear
    {
        [JsonPropertyName("yil")] public int Yil { get; init; }
        [JsonPropertyName("ramazanBayrami")] public string RamazanBayrami { get; init; } = "";
        [JsonPropertyName("kurbanBayrami")] public string KurbanBayrami { get; init; } = "";
        [JsonPropertyName("dogrulandi")] public bool Dogrulandi { get; init; }
    }
}
