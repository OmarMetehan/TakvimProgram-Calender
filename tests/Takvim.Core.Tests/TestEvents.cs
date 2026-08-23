using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Time;

namespace Takvim.Core.Tests;

/// <summary>Testlerde etkinlik kurmayı kısaltan yardımcılar.</summary>
internal static class TestEvents
{
    public static readonly TimeZoneService Zones = new();

    public const string Istanbul = "Europe/Istanbul";
    public const string Berlin = "Europe/Berlin";

    /// <summary>Zamanlı etkinlik kurar ve UTC önbellek alanlarını doldurur.</summary>
    public static Event Timed(
        string start,
        string end,
        string? rrule = null,
        string tzId = Istanbul,
        string? endTzId = null)
    {
        var startLocal = Parse(start);
        var endLocal = Parse(end);

        var ev = new Event
        {
            Uid = Guid.NewGuid().ToString("N"),
            ETag = "1",
            Title = "Test",
            StartLocal = startLocal,
            EndLocal = endLocal,
            StartTimeZoneId = tzId,
            EndTimeZoneId = endTzId ?? tzId,
            RecurrenceRule = rrule,
        };

        Recalculate(ev);
        return ev;
    }

    /// <summary>Tüm gün etkinliği kurar. Bitiş dışlayıcıdır: tek günlük etkinlikte ertesi gün.</summary>
    public static Event AllDay(string startDate, int days = 1, string? rrule = null, string tzId = Istanbul)
    {
        var start = LocalDate.FromDateTime(DateTime.Parse(startDate, System.Globalization.CultureInfo.InvariantCulture));
        var ev = new Event
        {
            Uid = Guid.NewGuid().ToString("N"),
            ETag = "1",
            Title = "Tüm gün",
            IsAllDay = true,
            StartLocal = start.AtMidnight(),
            EndLocal = start.PlusDays(days).AtMidnight(),
            StartTimeZoneId = null,
            EndTimeZoneId = null,
            RecurrenceRule = rrule,
        };

        // Tüm gün etkinliklerinde UTC önbelleği takvimin diliminden hesaplanır.
        ev.StartUtc = Zones.ToInstant(ev.StartLocal, tzId);
        ev.EndUtc = Zones.ToInstant(ev.EndLocal, tzId);
        return ev;
    }

    /// <summary>Bir seriden sapmış tek örnek üretir.</summary>
    public static Event Exception(Event series, string recurrenceId, string newStart, string newEnd)
    {
        var ev = Timed(newStart, newEnd, tzId: series.StartTimeZoneId ?? Istanbul);
        ev.Uid = series.Uid;
        ev.SeriesId = series.Id;
        ev.RecurrenceId = Parse(recurrenceId);
        ev.Title = series.Title + " (taşındı)";
        return ev;
    }

    /// <summary>Seriden silinmiş tek örnek: iptal işaretli istisna satırı.</summary>
    public static Event Cancelled(Event series, string recurrenceId)
    {
        var ev = Exception(series, recurrenceId, recurrenceId, recurrenceId);
        ev.Status = EventStatus.Cancelled;
        return ev;
    }

    public static void Recalculate(Event ev)
    {
        ev.StartUtc = Zones.ToInstant(ev.StartLocal, ev.StartTimeZoneId);
        ev.EndUtc = Zones.ToInstant(ev.EndLocal, ev.EndTimeZoneId ?? ev.StartTimeZoneId);
    }

    public static LocalDateTime Parse(string value)
        => LocalDateTime.FromDateTime(DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture));

    public static Instant Utc(string value, string tzId = Istanbul) => Zones.ToInstant(Parse(value), tzId);

    /// <summary>Örnek başlangıçlarını "2026-01-05 09:00" biçiminde döker; testlerde okunaklı karşılaştırma sağlar.</summary>
    public static string[] Starts(this IEnumerable<Takvim.Core.Recurrence.EventOccurrence> occurrences)
        => [.. occurrences.Select(o => o.StartLocal.ToString("uuuu-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture))];
}
