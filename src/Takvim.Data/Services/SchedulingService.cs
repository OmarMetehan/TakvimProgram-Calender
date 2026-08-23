using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Recurrence;
using Takvim.Core.Scheduling;

namespace Takvim.Data.Services;

/// <summary>
/// Zamanlama yardımcısının veri kaynağı.
/// <para>
/// İki satır türü üretir ve <see cref="ScheduleLane"/> ikisini de aynı biçimde
/// taşır: <see cref="GetLanesAsync"/> takvim başına, <see cref="GetPeopleLanesAsync"/>
/// kişi başına. Toplantıda katılımcı varsa kişi satırları, yoksa kendi
/// takvimlerinin satırları gösterilir.
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

        var occurrences = await ExpandAsync([.. calendarIds], from, to, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Kişilerin müsaitlik satırlarını çıkarır. Her satır bir kullanıcının
    /// <b>tüm</b> takvimlerini kapsar: bir kişi meşgulse hangi takviminden
    /// olduğu toplantı planlarken önemli değildir.
    /// <para>
    /// Ne gösterildiği izin modeline uyar: başkasının etkinliğinin başlığı
    /// buradan hiç okunmaz, yalnızca meşgul aralığı ve türü taşınır. Bu,
    /// serbest/meşgul paylaşımının tanımıdır.
    /// </para>
    /// </summary>
    /// <param name="participants">Satır üretilecek kullanıcılar ve zorunluluk durumları.</param>
    public async Task<List<ScheduleLane>> GetPeopleLanesAsync(
        IReadOnlyList<(Guid UserId, string Name, bool IsRequired)> participants,
        Instant from,
        Instant to,
        LocalDate date,
        string zoneId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(participants);
        if (participants.Count == 0) return [];

        var userIds = participants.Select(p => p.UserId).ToList();

        var calendars = await db.Calendars
            .AsNoTracking()
            .Where(c => userIds.Contains(c.OwnerUserId) && c.DeletedAt == null)
            .Select(c => new { c.Id, c.OwnerUserId })
            .ToListAsync(ct).ConfigureAwait(false);

        var calendarOwner = calendars.ToDictionary(c => c.Id, c => c.OwnerUserId);
        var calendarIds = calendars.Select(c => c.Id).ToList();

        var occurrences = calendarIds.Count == 0
            ? []
            : await ExpandAsync(calendarIds, from, to, ct).ConfigureAwait(false);

        var byUser = occurrences
            .Where(o => calendarOwner.ContainsKey(o.Source.CalendarId))
            .ToLookup(o => calendarOwner[o.Source.CalendarId]);

        var lanes = new List<ScheduleLane>(participants.Count);

        foreach (var (userId, name, isRequired) in participants)
        {
            var schedule = await workSchedule.GetDayAsync(userId, date, ct).ConfigureAwait(false);

            lanes.Add(new ScheduleLane(
                userId,
                name,
                FreeBusy.Merge(byUser[userId]
                    .Select(o => new BusyInterval(o.StartUtc, o.EndUtc, o.Source.Availability))),
                isRequired,
                ToInstantRange(schedule, date, zoneId)));
        }

        return lanes;
    }

    /// <summary>Verilen takvimlerdeki örnekleri pencerede açar.</summary>
    private async Task<List<Takvim.Core.Recurrence.EventOccurrence>> ExpandAsync(
        List<Guid> calendarIds, Instant from, Instant to, CancellationToken ct)
    {
        var roots = await db.Events
            .AsNoTracking()
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

        return expander.ExpandMany(roots, exceptions.ToLookup(e => e.SeriesId!.Value), from, to);
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
