using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Recurrence;
using Takvim.Core.Scheduling;

namespace Takvim.Data.Services;

/// <summary>
/// Zamanlama yardımcısının veri kaynağı: seçilen takvimlerin meşguliyet
/// aralıklarını çıkarır.
/// <para>
/// Faz 1 tek kullanıcılı olduğu için satırlar takvimlerdir. Katılımcılar
/// eklendiğinde aynı yapı kişileri de taşıyacak: <see cref="ScheduleLane"/>
/// kimliğin kullanıcı mı takvim mi olduğunu bilmez.
/// </para>
/// </summary>
public sealed class SchedulingService(
    TakvimDbContext db,
    RecurrenceExpander expander,
    WorkScheduleService workSchedule)
{
    /// <summary>
    /// Verilen takvimlerin, verilen pencere içindeki meşguliyet satırlarını çıkarır.
    /// </summary>
    /// <param name="calendarIds">Satır üretilecek takvimler.</param>
    /// <param name="from">Pencerenin başlangıcı.</param>
    /// <param name="to">Pencerenin bitişi.</param>
    /// <param name="userId">Mesai saatlerinin okunacağı kullanıcı.</param>
    /// <param name="date">Mesai saatleri için gün.</param>
    /// <param name="zoneId">Mesai saatlerinin çevrileceği zaman dilimi.</param>
    public async Task<List<ScheduleLane>> GetLanesAsync(
        IReadOnlyList<Guid> calendarIds,
        Instant from,
        Instant to,
        Guid userId,
        LocalDate date,
        string zoneId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(calendarIds);
        if (calendarIds.Count == 0) return [];

        var calendars = await db.Calendars
            .AsNoTracking()
            .Where(c => calendarIds.Contains(c.Id) && c.DeletedAt == null)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .ToListAsync(ct).ConfigureAwait(false);

        var roots = await db.Events
            .AsNoTracking()
            .Include(e => e.Calendar)
            .Where(e => e.DeletedAt == null
                        && e.SeriesId == null
                        && e.Status != EventStatus.Cancelled
                        && calendarIds.Contains(e.CalendarId)
                        && ((e.RecurrenceRule == null && e.StartUtc < to && e.EndUtc > from)
                            || (e.RecurrenceRule != null && e.StartUtc < to
                                && (e.SeriesEndUtc == null || e.SeriesEndUtc > from))))
            .ToListAsync(ct).ConfigureAwait(false);

        var seriesIds = roots.Where(e => e.RecurrenceRule != null).Select(e => e.Id).ToList();
        var exceptions = seriesIds.Count == 0
            ? []
            : await db.Events
                .AsNoTracking()
                .Where(e => e.SeriesId != null && seriesIds.Contains(e.SeriesId.Value) && e.DeletedAt == null)
                .ToListAsync(ct).ConfigureAwait(false);

        var occurrences = expander.ExpandMany(roots, exceptions.ToLookup(e => e.SeriesId!.Value), from, to);
        var byCalendar = occurrences.ToLookup(o => o.Source.CalendarId);

        var schedule = await workSchedule.GetDayAsync(userId, date, ct).ConfigureAwait(false);
        var hours = ToInstantRange(schedule, date, zoneId);

        return
        [
            .. calendars.Select(calendar => new ScheduleLane(
                calendar.Id,
                calendar.Name,
                FreeBusy.Merge(byCalendar[calendar.Id]
                    .Select(o => new BusyInterval(o.StartUtc, o.EndUtc, o.Source.Availability))),
                IsRequired: true,
                WorkingHours: hours))
        ];
    }

    /// <summary>Mesai aralığını mutlak zamana çevirir; çalışılmayan günde null döner.</summary>
    private static (Instant Start, Instant End)? ToInstantRange(
        DaySchedule schedule, LocalDate date, string zoneId)
    {
        if (!schedule.IsWorkingDay) return null;

        var zone = NodaTime.DateTimeZoneProviders.Tzdb.GetZoneOrNull(zoneId)
                   ?? NodaTime.DateTimeZoneProviders.Tzdb["Europe/Istanbul"];

        var start = (date + schedule.Start).InZoneLeniently(zone).ToInstant();
        var end = (date + schedule.End).InZoneLeniently(zone).ToInstant();

        return (start, end);
    }
}
