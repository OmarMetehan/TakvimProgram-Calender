using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Localization;

namespace Takvim.Data.Services;

/// <summary>
/// Bir günün mesai düzeni: ızgaranın hangi bölümünü soluk çizeceğini belirler.
/// </summary>
/// <param name="Date">Gün.</param>
/// <param name="IsWorkingDay">O gün çalışılıyor mu. Resmi tatiller de burada false olur.</param>
/// <param name="Start">Mesai başlangıcı.</param>
/// <param name="End">Mesai bitişi. Arefe günlerinde tatil kuralı bunu öne çeker.</param>
/// <param name="BreakStart">Öğle arası başlangıcı.</param>
/// <param name="BreakEnd">Öğle arası bitişi.</param>
/// <param name="Location">O günkü çalışma konumu.</param>
/// <param name="LocationNote">Şube adı gibi serbest metin.</param>
/// <param name="HolidayName">Gün resmi tatilse adı; değilse null.</param>
public sealed record DaySchedule(
    LocalDate Date,
    bool IsWorkingDay,
    LocalTime Start,
    LocalTime End,
    LocalTime? BreakStart,
    LocalTime? BreakEnd,
    WorkLocation Location,
    string? LocationNote,
    string? HolidayName)
{
    /// <summary>Mesainin gün içindeki oransal başlangıcı (0-1).</summary>
    public double StartRatio => (Start.Hour * 60 + Start.Minute) / (24.0 * 60);

    /// <summary>Mesainin gün içindeki oransal bitişi (0-1).</summary>
    public double EndRatio => (End.Hour * 60 + End.Minute) / (24.0 * 60);

    public double? BreakStartRatio => BreakStart is { } value ? (value.Hour * 60 + value.Minute) / (24.0 * 60) : null;

    public double? BreakEndRatio => BreakEnd is { } value ? (value.Hour * 60 + value.Minute) / (24.0 * 60) : null;

    public string LocationLabel => Location switch
    {
        WorkLocation.Office => "Ofis",
        WorkLocation.Home => "Evden",
        WorkLocation.Branch => string.IsNullOrWhiteSpace(LocationNote) ? "Şube" : LocationNote,
        WorkLocation.Away => "Çalışmıyor",
        _ => string.Empty,
    };
}

/// <summary>
/// Çalışma düzenini okur ve yazar.
/// <para>
/// Üç kaynak birleştirilir: haftalık mesai tanımı, o güne özel çalışma konumu
/// kaydı ve resmi tatil takvimi. Tatil kuralı diğerlerini ezer — arefe günü
/// mesai 13:00'te biter, tam gün tatilde hiç çalışılmaz.
/// </para>
/// </summary>
public sealed class WorkScheduleService(TakvimDbContext db, TurkishHolidays holidays)
{
    /// <summary>Bir tarih aralığının günlük mesai düzenini verir.</summary>
    public async Task<Dictionary<LocalDate, DaySchedule>> GetRangeAsync(
        Guid userId,
        LocalDate fromInclusive,
        LocalDate toExclusive,
        CancellationToken ct = default)
    {
        var weekly = await db.WorkingHours
            .AsNoTracking()
            .Where(w => w.UserId == userId)
            .ToDictionaryAsync(w => w.DayOfWeek, ct).ConfigureAwait(false);

        var locations = await db.WorkLocations
            .AsNoTracking()
            .Where(l => l.UserId == userId && l.Date >= fromInclusive && l.Date < toExclusive)
            .ToDictionaryAsync(l => l.Date, ct).ConfigureAwait(false);

        var result = new Dictionary<LocalDate, DaySchedule>();

        for (var date = fromInclusive; date < toExclusive; date = date.PlusDays(1))
        {
            result[date] = Compose(date, weekly, locations);
        }

        return result;
    }

    public async Task<DaySchedule> GetDayAsync(Guid userId, LocalDate date, CancellationToken ct = default)
    {
        var range = await GetRangeAsync(userId, date, date.PlusDays(1), ct).ConfigureAwait(false);
        return range[date];
    }

    private DaySchedule Compose(
        LocalDate date,
        Dictionary<IsoDayOfWeek, WorkingHours> weekly,
        Dictionary<LocalDate, WorkLocationEntry> locations)
    {
        var defaults = weekly.GetValueOrDefault(date.DayOfWeek);
        var isWorkingDay = defaults?.IsWorkingDay ?? date.DayOfWeek <= IsoDayOfWeek.Friday;
        var start = defaults?.Start ?? new LocalTime(9, 0);
        var end = defaults?.End ?? new LocalTime(18, 0);

        var dayHolidays = holidays.On(date).ToList();
        var fullDayOff = dayHolidays.FirstOrDefault(h => h.IsDayOff);
        var halfDay = dayHolidays.FirstOrDefault(h => h.Kind == HolidayKind.HalfDayEve);

        if (fullDayOff is not null)
        {
            isWorkingDay = false;
        }
        else if (halfDay?.WorkEndsAt is { } eveEnd && eveEnd < end)
        {
            // Arefe: mesai öğleden sonra biter.
            end = eveEnd;
        }

        var entry = locations.GetValueOrDefault(date);
        var location = entry?.Location
            ?? (isWorkingDay ? WorkLocation.Unspecified : WorkLocation.Away);

        return new DaySchedule(
            date,
            isWorkingDay,
            start,
            end,
            defaults?.BreakStart,
            defaults?.BreakEnd,
            location,
            entry?.Note,
            (fullDayOff ?? halfDay)?.Name);
    }

    // ------------------------------------------------------------------
    // Yazma
    // ------------------------------------------------------------------

    /// <summary>Haftalık mesai tanımını günceller.</summary>
    public async Task SaveWeeklyAsync(
        Guid userId, IEnumerable<WorkingHours> days, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(days);

        var existing = await db.WorkingHours
            .Where(w => w.UserId == userId)
            .ToDictionaryAsync(w => w.DayOfWeek, ct).ConfigureAwait(false);

        foreach (var day in days)
        {
            if (existing.TryGetValue(day.DayOfWeek, out var row))
            {
                row.IsWorkingDay = day.IsWorkingDay;
                row.Start = day.Start;
                row.End = day.End;
                row.BreakStart = day.BreakStart;
                row.BreakEnd = day.BreakEnd;
            }
            else
            {
                day.UserId = userId;
                db.WorkingHours.Add(day);
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Bir günün çalışma konumunu ayarlar. <see cref="WorkLocation.Unspecified"/>
    /// verilirse kayıt silinir; her gün için satır tutulmaz.
    /// </summary>
    public async Task SetLocationAsync(
        Guid userId, LocalDate date, WorkLocation location, string? note = null, CancellationToken ct = default)
    {
        var entry = await db.WorkLocations
            .FirstOrDefaultAsync(l => l.UserId == userId && l.Date == date, ct).ConfigureAwait(false);

        if (location == WorkLocation.Unspecified)
        {
            if (entry is not null) db.WorkLocations.Remove(entry);
        }
        else if (entry is null)
        {
            db.WorkLocations.Add(new WorkLocationEntry
            {
                UserId = userId,
                Date = date,
                Location = location,
                Note = note,
            });
        }
        else
        {
            entry.Location = location;
            entry.Note = note;
            entry.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
