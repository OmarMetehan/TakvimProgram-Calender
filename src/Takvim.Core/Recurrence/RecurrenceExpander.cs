using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using NodaTime;
using NodaTime.Text;
using Takvim.Core.Domain;
using Takvim.Core.Localization;
using Takvim.Core.Time;

// Ical.Net ve NodaTime aynı adları taşıyan tipler sunar; bu dosyada zaman
// aritmetiği daima NodaTime tarafındadır.
using Duration = NodaTime.Duration;
using Period = NodaTime.Period;

namespace Takvim.Core.Recurrence;

/// <summary>
/// Bir seriyi verilen tarih aralığında somut örneklere açar.
/// RRULE üretimi Ical.Net'e bırakılır; istisnaların birleştirilmesi ve
/// süre aritmetiği burada yapılır.
/// </summary>
public sealed class RecurrenceExpander(TimeZoneService timeZones, TurkishHolidays? holidays = null)
{
    /// <summary>
    /// Tatil ertelemesinde en fazla kaç gün ileri bakılacağı. Ardışık tatiller
    /// (dört günlük Kurban Bayramı gibi) aşılabilmeli, ama bozuk bir veri
    /// sonsuz döngüye yol açmamalı.
    /// </summary>
    private const int MaxHolidayShiftDays = 14;

    /// <summary>
    /// Bozuk ya da aşırı yoğun bir kuralın görünümü kilitlemesini önleyen üst sınır.
    /// Tek bir seri, tek bir pencerede bundan fazla örnek üretemez.
    /// </summary>
    public const int MaxOccurrencesPerWindow = 2000;

    private static readonly LocalDateTimePattern DateListFormat =
        LocalDateTimePattern.CreateWithInvariantCulture("uuuu-MM-ddTHH:mm:ss");

    /// <summary>
    /// <paramref name="root"/> serisini <paramref name="fromInclusive"/> ile
    /// <paramref name="toExclusive"/> arasına değen örneklere açar.
    /// </summary>
    /// <param name="root">Seri kökü veya tekil etkinlik.</param>
    /// <param name="exceptions">Bu seriye ait istisna satırları (taşınmış/değiştirilmiş örnekler).</param>
    /// <param name="fromInclusive">Pencerenin başlangıcı, dahil.</param>
    /// <param name="toExclusive">Pencerenin bitişi, hariç.</param>
    public IEnumerable<EventOccurrence> Expand(
        Event root,
        IReadOnlyCollection<Event>? exceptions,
        Instant fromInclusive,
        Instant toExclusive)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (toExclusive <= fromInclusive) yield break;

        var overrides = BuildOverrideMap(exceptions);
        var excluded = ParseDateList(root.ExDates);

        // 1) Tekrarlamayan etkinlik: tek örnek.
        if (string.IsNullOrWhiteSpace(root.RecurrenceRule) && string.IsNullOrWhiteSpace(root.RDates))
        {
            if (root.StartUtc < toExclusive && root.EndUtc > fromInclusive)
            {
                yield return new EventOccurrence
                {
                    Source = root,
                    StartLocal = root.StartLocal,
                    EndLocal = root.EndLocal,
                    StartUtc = root.StartUtc,
                    EndUtc = root.EndUtc,
                };
            }
            yield break;
        }

        // 2) Serinin nominal süresi. "Nominal" olması önemlidir: 09:00-10:00 bir toplantı
        //    yaz saati geçişinden sonra da 09:00-10:00 kalmalıdır, 08:00-09:00 olmamalıdır.
        var nominalLength = NominalLength(root.StartLocal, root.EndLocal);

        // 3) Pencerenin başında hâlâ süren örnekleri kaçırmamak için değerlendirmeyi
        //    bir örnek boyu geriden başlatırız. Tatil ertelemesi açıksa daha da
        //    geriden: pencereden önce üretilen bir örnek, tatil nedeniyle
        //    pencerenin içine kaymış olabilir.
        var lookBack = MaxDuration(root, nominalLength);

        if (root.HolidayBehavior == HolidayBehavior.MoveToNextWorkingDay)
        {
            lookBack += Duration.FromDays(MaxHolidayShiftDays);
        }

        var searchFrom = fromInclusive - lookBack;

        var produced = 0;
        foreach (var startLocal in GenerateStarts(root, searchFrom, toExclusive))
        {
            if (++produced > MaxOccurrencesPerWindow) yield break;

            if (excluded.Contains(startLocal)) continue;
            if (overrides.ContainsKey(startLocal)) continue; // istisna ayrıca yayılır

            // Tatil kuralı, örnek üretildikten sonra uygulanır: kuralın kendisi
            // (RRULE) tatilden habersizdir, tatil takvimi ayrı bir kaynaktır.
            var adjusted = ApplyHolidayRule(root, startLocal);
            if (adjusted is not { } effectiveStart) continue;

            var endLocal = effectiveStart + nominalLength;
            var startUtc = ToInstant(effectiveStart, root.StartTimeZoneId, root);
            var endUtc = ToInstant(endLocal, root.EndTimeZoneId ?? root.StartTimeZoneId, root);

            // Erteleme pencereden ileri taşımış olabilir; akışı kesmek yerine
            // bu örneği atlarız, sonraki örnek pencerede olabilir.
            if (startUtc >= toExclusive) continue;
            if (endUtc <= fromInclusive) continue;

            yield return new EventOccurrence
            {
                Source = root,
                StartLocal = effectiveStart,
                EndLocal = endLocal,
                StartUtc = startUtc,
                EndUtc = endUtc,
                // Örneğin seri içindeki kimliği özgün saattir; ertelenmiş olsa da
                // "bu etkinliği düzenle" doğru örneği bulabilmeli.
                RecurrenceId = startLocal,
            };
        }

        // 4) İstisnalar: taşınmış bir örnek pencereye dışarıdan girebileceği için
        //    üretim döngüsünden bağımsız değerlendirilir.
        foreach (var (recurrenceId, exception) in overrides)
        {
            if (exception.Status == EventStatus.Cancelled) continue;   // silinmiş tek örnek
            if (exception.DeletedAt is not null) continue;
            if (exception.StartUtc >= toExclusive || exception.EndUtc <= fromInclusive) continue;

            yield return new EventOccurrence
            {
                Source = exception,
                StartLocal = exception.StartLocal,
                EndLocal = exception.EndLocal,
                StartUtc = exception.StartUtc,
                EndUtc = exception.EndUtc,
                RecurrenceId = recurrenceId,
                IsException = true,
            };
        }
    }

    /// <summary>Birden çok seriyi tek bir sıralı akışta açar.</summary>
    public List<EventOccurrence> ExpandMany(
        IEnumerable<Event> roots,
        ILookup<Guid, Event> exceptionsBySeries,
        Instant fromInclusive,
        Instant toExclusive)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(exceptionsBySeries);

        var result = new List<EventOccurrence>();
        foreach (var root in roots)
        {
            var exceptions = exceptionsBySeries[root.Id].ToList();
            result.AddRange(Expand(root, exceptions, fromInclusive, toExclusive));
        }

        result.Sort(static (a, b) =>
        {
            var byStart = a.StartUtc.CompareTo(b.StartUtc);
            if (byStart != 0) return byStart;
            // Aynı anda başlayanlarda uzun olan önce: ızgarada daha okunaklı yerleşir.
            var byLength = b.EndUtc.CompareTo(a.EndUtc);
            return byLength != 0 ? byLength : string.CompareOrdinal(a.Source.Title, b.Source.Title);
        });
        return result;
    }

    /// <summary>
    /// Serinin son örneğinin bitişini hesaplar; süresiz seriler için null döner.
    /// <see cref="Event.SeriesEndUtc"/> alanı bununla doldurulur, böylece aralık
    /// sorguları sonlu serileri erkenden eleyebilir.
    /// </summary>
    public Instant? CalculateSeriesEnd(Event root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (string.IsNullOrWhiteSpace(root.RecurrenceRule)) return root.EndUtc;

        var pattern = new RecurrencePattern(root.RecurrenceRule);
        if (pattern.Count is null && pattern.Until is null) return null; // süresiz

        var nominalLength = NominalLength(root.StartLocal, root.EndLocal);
        LocalDateTime? last = null;
        var seen = 0;

        foreach (var startLocal in GenerateStarts(root, root.StartUtc, Instant.MaxValue))
        {
            last = startLocal;
            if (++seen > MaxOccurrencesPerWindow * 10) break;
        }

        if (last is null) return root.EndUtc;
        return ToInstant(last.Value + nominalLength, root.EndTimeZoneId ?? root.StartTimeZoneId, root);
    }

    // ------------------------------------------------------------------

    /// <summary>RRULE ve RDATE'e göre başlangıç saatlerini üretir. Tembel çalışır.</summary>
    private IEnumerable<LocalDateTime> GenerateStarts(Event root, Instant searchFrom, Instant toExclusive)
    {
        var calendarEvent = BuildIcalEvent(root);
        var zoneId = root.StartTimeZoneId ?? TimeZoneService.DefaultZoneId;

        var searchLocal = timeZones.ToLocal(searchFrom, zoneId);
        var evaluationStart = root.IsAllDay
            ? new CalDateTime(searchLocal.Date.ToDateOnly())
            : new CalDateTime(searchLocal.Year, searchLocal.Month, searchLocal.Day,
                              searchLocal.Hour, searchLocal.Minute, searchLocal.Second, zoneId);

        foreach (var occurrence in calendarEvent.GetOccurrences(evaluationStart, options: null))
        {
            var start = occurrence.Period.StartTime;
            var local = root.IsAllDay
                ? new LocalDateTime(start.Year, start.Month, start.Day, 0, 0)
                : new LocalDateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute, start.Second);

            // Tembel akışı burada keseriz; aksi hâlde süresiz seriler sonsuza kadar üretir.
            if (toExclusive != Instant.MaxValue && ToInstant(local, root.StartTimeZoneId, root) >= toExclusive)
                yield break;

            yield return local;
        }
    }

    /// <summary>Etkinliğimizi Ical.Net'in değerlendirebileceği bileşene çevirir.</summary>
    private static CalendarEvent BuildIcalEvent(Event root)
    {
        var zoneId = root.StartTimeZoneId ?? TimeZoneService.DefaultZoneId;
        var calendarEvent = new CalendarEvent();

        if (root.IsAllDay)
        {
            calendarEvent.DtStart = new CalDateTime(root.StartLocal.Date.ToDateOnly());
            calendarEvent.DtEnd = new CalDateTime(root.EndLocal.Date.ToDateOnly());
        }
        else
        {
            calendarEvent.DtStart = new CalDateTime(
                root.StartLocal.Year, root.StartLocal.Month, root.StartLocal.Day,
                root.StartLocal.Hour, root.StartLocal.Minute, root.StartLocal.Second, zoneId);
            calendarEvent.DtEnd = new CalDateTime(
                root.EndLocal.Year, root.EndLocal.Month, root.EndLocal.Day,
                root.EndLocal.Hour, root.EndLocal.Minute, root.EndLocal.Second,
                root.EndTimeZoneId ?? zoneId);
        }

        if (!string.IsNullOrWhiteSpace(root.RecurrenceRule))
            calendarEvent.RecurrenceRule = new RecurrencePattern(root.RecurrenceRule);

        foreach (var date in ParseDateList(root.RDates))
        {
            calendarEvent.RecurrenceDates.Add(root.IsAllDay
                ? new CalDateTime(date.Date.ToDateOnly())
                : new CalDateTime(date.Year, date.Month, date.Day, date.Hour, date.Minute, date.Second, zoneId));
        }

        return calendarEvent;
    }

    /// <summary>
    /// Tatil kuralını uygular. Örnek atlanacaksa null, ertelenecekse yeni
    /// başlangıç, kural yoksa gelen başlangıç döner.
    /// </summary>
    private LocalDateTime? ApplyHolidayRule(Event root, LocalDateTime startLocal)
    {
        if (root.HolidayBehavior == HolidayBehavior.Include || holidays is null) return startLocal;
        if (!holidays.IsDayOff(startLocal.Date)) return startLocal;

        if (root.HolidayBehavior == HolidayBehavior.Skip) return null;

        // Erteleme: tatil olmayan ve hafta sonuna denk gelmeyen ilk güne taşınır.
        var candidate = startLocal.Date;

        for (var step = 0; step < MaxHolidayShiftDays; step++)
        {
            candidate = candidate.PlusDays(1);

            var weekend = candidate.DayOfWeek is IsoDayOfWeek.Saturday or IsoDayOfWeek.Sunday;
            if (!weekend && !holidays.IsDayOff(candidate)) return candidate + startLocal.TimeOfDay;
        }

        // Bu kadar uzun bir tatil dizisi gerçekte yok; veri bozuksa örnek atlanır.
        return null;
    }

    private Instant ToInstant(LocalDateTime local, string? tzId, Event root)
        => timeZones.ToInstant(local, tzId ?? root.Calendar?.TimeZoneId ?? TimeZoneService.DefaultZoneId);

    /// <summary>
    /// Duvar saati cinsinden süre. Gün ve altı birimlerle ölçülür; ay ve yıl kullanılmaz,
    /// çünkü ay uzunluğu değişken olduğundan süre ay bazında taşınamaz.
    /// </summary>
    private static Period NominalLength(LocalDateTime start, LocalDateTime end)
        => Period.Between(start, end,
            PeriodUnits.Days | PeriodUnits.Hours | PeriodUnits.Minutes | PeriodUnits.Seconds);

    private static Duration MaxDuration(Event root, Period nominalLength)
    {
        var actual = root.EndUtc - root.StartUtc;
        var nominal = Duration.FromDays(nominalLength.Days)
                    + Duration.FromHours(nominalLength.Hours)
                    + Duration.FromMinutes(nominalLength.Minutes)
                    + Duration.FromSeconds(nominalLength.Seconds);
        return actual > nominal ? actual : nominal;
    }

    private static Dictionary<LocalDateTime, Event> BuildOverrideMap(IReadOnlyCollection<Event>? exceptions)
    {
        var map = new Dictionary<LocalDateTime, Event>();
        if (exceptions is null) return map;
        foreach (var exception in exceptions)
        {
            if (exception.RecurrenceId is { } id) map[id] = exception;
        }
        return map;
    }

    /// <summary>EXDATE / RDATE metin listesini çözer. Biçim: ISO yerel tarih-saat, virgülle ayrılmış.</summary>
    public static HashSet<LocalDateTime> ParseDateList(string? value)
    {
        var set = new HashSet<LocalDateTime>();
        if (string.IsNullOrWhiteSpace(value)) return set;

        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parsed = DateListFormat.Parse(part);
            if (parsed.Success) set.Add(parsed.Value);
        }
        return set;
    }

    public static string FormatDateList(IEnumerable<LocalDateTime> dates)
    {
        ArgumentNullException.ThrowIfNull(dates);
        return string.Join(',', dates.OrderBy(d => d).Select(DateListFormat.Format));
    }
}
