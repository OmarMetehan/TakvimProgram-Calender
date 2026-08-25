using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Scheduling;
using Takvim.Core.Text;
using Takvim.Core.Time;

namespace Takvim.Data.Services;

/// <summary>Randevu sayfası tanımlama girdisi.</summary>
public sealed record AppointmentScheduleInput
{
    public required string Name { get; init; }
    public string? Slug { get; init; }
    public string? Description { get; init; }

    public required Guid CalendarId { get; init; }

    public int DurationMinutes { get; init; } = 30;
    public int BufferMinutes { get; init; } = 5;
    public int MinimumNoticeHours { get; init; } = 4;
    public int MaximumAdvanceDays { get; init; } = 30;
    public int? MaximumPerDay { get; init; }

    public string? LocationText { get; init; }
    public string? OnlineMeetingProvider { get; init; }

    /// <summary>Haftalık pencereler; boşsa sayfa randevu üretmez.</summary>
    public IReadOnlyList<(IsoDayOfWeek Day, LocalTime Start, LocalTime End)> Windows { get; init; } = [];
}

/// <summary>Randevuya açık tek bir dilim.</summary>
public sealed record AppointmentSlot(Instant StartUtc, Instant EndUtc, LocalDateTime StartLocal);

/// <summary>Randevu alma denemesinin sonucu.</summary>
public sealed record AppointmentResult(Appointment? Appointment, string? Error)
{
    public bool Success => Error is null;
}

/// <summary>
/// Randevu sayfaları.
/// <para>
/// Dilim üretimi iki kaynağı birleştirir: sayfanın haftalık pencereleri ve
/// sahibinin gerçek meşguliyeti. Yalnızca boşluklara bakmak yanlış olurdu —
/// takvimi boş olan biri o saatte randevu kabul etmiyor olabilir; yalnızca
/// pencerelere bakmak da yanlış olurdu — pencere içinde başka bir toplantısı
/// olabilir.
/// </para>
/// </summary>
public sealed class AppointmentService(
    TakvimDbContext db,
    CalendarQueryService query,
    TimeZoneService zones,
    EventService events,
    IClock clock)
{
    /// <summary>Bir sorguda üretilecek en fazla dilim; bozuk tanım akışı kilitlemesin.</summary>
    public const int MaxSlotsPerQuery = 500;

    // ==================================================================
    // Sayfa tanımları
    // ==================================================================

    public Task<List<AppointmentSchedule>> GetSchedulesAsync(
        Guid ownerUserId, CancellationToken ct = default)
        => db.AppointmentSchedules
            .AsNoTracking()
            .Include(s => s.Windows)
            .Where(s => s.OwnerUserId == ownerUserId)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

    public Task<AppointmentSchedule?> FindAsync(Guid scheduleId, CancellationToken ct = default)
        => db.AppointmentSchedules
            .AsNoTracking()
            .Include(s => s.Windows)
            .Include(s => s.Owner)
            .FirstOrDefaultAsync(s => s.Id == scheduleId, ct);

    /// <summary>Adres satırındaki kısa ada göre bulur; yerel ağa açılan sayfa bunu kullanır.</summary>
    public Task<AppointmentSchedule?> FindBySlugAsync(string slug, CancellationToken ct = default)
        => db.AppointmentSchedules
            .AsNoTracking()
            .Include(s => s.Windows)
            .Include(s => s.Owner)
            .FirstOrDefaultAsync(s => s.Slug == slug && s.IsActive, ct);

    public async Task<AppointmentSchedule> CreateAsync(
        AppointmentScheduleInput input, Guid ownerUserId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Name);

        var schedule = new AppointmentSchedule
        {
            OwnerUserId = ownerUserId,
            Name = input.Name.Trim(),
            Slug = await UniqueSlugAsync(input.Slug ?? input.Name, null, ct).ConfigureAwait(false),
            CreatedAt = clock.GetCurrentInstant().ToDateTimeOffset(),
        };

        ApplyInput(schedule, input);

        foreach (var window in BuildWindows(schedule.Id, input)) schedule.Windows.Add(window);

        db.AppointmentSchedules.Add(schedule);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return schedule;
    }

    public async Task<AppointmentSchedule?> UpdateAsync(
        Guid scheduleId, AppointmentScheduleInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Name);

        // Pencereler bilerek yüklenmez: aşağıda tek bir SQL ile silinecekler
        // ve izlenen kopyaları kalırsa EF onları ikinci kez silmeye çalışır.
        var schedule = await db.AppointmentSchedules
            .FirstOrDefaultAsync(s => s.Id == scheduleId, ct).ConfigureAwait(false);

        if (schedule is null) return null;

        schedule.Name = input.Name.Trim();

        if (!string.IsNullOrWhiteSpace(input.Slug) && input.Slug != schedule.Slug)
        {
            schedule.Slug = await UniqueSlugAsync(input.Slug, scheduleId, ct).ConfigureAwait(false);
        }

        // Pencereler tümüyle yenilenir: azını güncelleyip çoğunu silmek yerine
        // tek parça yazmak, arayüzün gönderdiğiyle kaydın birebir eşleşmesini
        // garanti eder.
        await db.AppointmentWindows
            .Where(w => w.ScheduleId == scheduleId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        ApplyInput(schedule, input);

        // Pencereler DbSet üzerinden eklenir: kimlikleri nesne kurulurken
        // atandığı için, izlenen bir üst kayda gezinme özelliğinden eklenseler
        // EF onları "var olan satır" sayıp INSERT yerine UPDATE üretir.
        db.AppointmentWindows.AddRange(BuildWindows(scheduleId, input));

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return schedule;
    }

    public async Task SetActiveAsync(Guid scheduleId, bool isActive, CancellationToken ct = default)
    {
        var schedule = await db.AppointmentSchedules
            .FirstOrDefaultAsync(s => s.Id == scheduleId, ct).ConfigureAwait(false);

        if (schedule is null) return;

        schedule.IsActive = isActive;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Sayfayı siler. Alınmış randevular takvimde kalır.</summary>
    public async Task DeleteAsync(Guid scheduleId, CancellationToken ct = default)
        => await db.AppointmentSchedules
            .Where(s => s.Id == scheduleId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

    // ==================================================================
    // Dilimler
    // ==================================================================

    /// <summary>
    /// Verilen gün aralığında randevuya açık dilimleri üretir.
    /// <para>
    /// Sıra şudur: haftalık pencereler dilimlere bölünür, sahibin meşgul
    /// aralıkları düşülür, çok yakın ve çok uzak olanlar elenir, günlük sınır
    /// uygulanır.
    /// </para>
    /// </summary>
    public async Task<List<AppointmentSlot>> GetSlotsAsync(
        Guid scheduleId,
        LocalDate fromInclusive,
        LocalDate toExclusive,
        CancellationToken ct = default)
    {
        var schedule = await db.AppointmentSchedules
            .AsNoTracking()
            .Include(s => s.Windows)
            .Include(s => s.Owner)
            .FirstOrDefaultAsync(s => s.Id == scheduleId, ct).ConfigureAwait(false);

        if (schedule is null || schedule.Windows.Count == 0) return [];

        var zoneId = schedule.Owner?.TimeZoneId ?? TimeZoneService.DefaultZoneId;
        var now = clock.GetCurrentInstant();

        // Çok yakın ve çok uzak günler baştan kırpılır; boşuna dilim üretilmez.
        var earliest = now.Plus(Duration.FromHours(schedule.MinimumNoticeHours));
        var latest = zones.ToLocal(now, zoneId).Date.PlusDays(schedule.MaximumAdvanceDays);

        if (toExclusive > latest.PlusDays(1)) toExclusive = latest.PlusDays(1);
        if (fromInclusive >= toExclusive) return [];

        var windowStart = zones.ToInstant(fromInclusive.AtMidnight(), zoneId);
        var windowEnd = zones.ToInstant(toExclusive.AtMidnight(), zoneId);

        var busy = await LoadBusyAsync(schedule, windowStart, windowEnd, ct).ConfigureAwait(false);
        var takenPerDay = await CountPerDayAsync(scheduleId, windowStart, windowEnd, zoneId, ct)
            .ConfigureAwait(false);

        var slots = new List<AppointmentSlot>();
        var byDay = schedule.Windows.ToLookup(w => w.DayOfWeek);

        for (var date = fromInclusive; date < toExclusive; date = date.PlusDays(1))
        {
            var used = takenPerDay.GetValueOrDefault(date);
            if (schedule.MaximumPerDay is { } cap && used >= cap) continue;

            var remaining = schedule.MaximumPerDay is { } limit ? limit - used : int.MaxValue;

            foreach (var window in byDay[date.DayOfWeek].OrderBy(w => w.Start))
            {
                foreach (var slot in SlotsIn(schedule, date, window, zoneId))
                {
                    if (slots.Count >= MaxSlotsPerQuery) return slots;
                    if (remaining <= 0) break;

                    if (slot.StartUtc < earliest) continue;
                    if (Overlaps(busy, slot)) continue;

                    slots.Add(slot);
                    remaining--;
                }
            }
        }

        return slots;
    }

    // ==================================================================
    // Randevu alma
    // ==================================================================

    /// <summary>
    /// Randevuyu alır ve takvime yazar.
    /// <para>
    /// Dilimin hâlâ açık olduğu <b>alma anında</b> yeniden denetlenir: iki
    /// kişi aynı dilimi aynı anda görüyor olabilir ve listeyi göstermek bir
    /// söz vermek değildir.
    /// </para>
    /// </summary>
    public async Task<AppointmentResult> BookAsync(
        Guid scheduleId,
        Instant startUtc,
        string guestName,
        string? guestEmail = null,
        string? note = null,
        Guid? bookedByUserId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guestName);

        var schedule = await db.AppointmentSchedules
            .AsNoTracking()
            .Include(s => s.Windows)
            .Include(s => s.Owner)
            .FirstOrDefaultAsync(s => s.Id == scheduleId && s.IsActive, ct).ConfigureAwait(false);

        if (schedule is null) return new AppointmentResult(null, "Randevu sayfası bulunamadı.");

        var zoneId = schedule.Owner?.TimeZoneId ?? TimeZoneService.DefaultZoneId;
        var date = zones.ToLocal(startUtc, zoneId).Date;

        // Listeyi göstermek söz vermek değildir; dilim hâlâ açık mı diye bakılır.
        var open = await GetSlotsAsync(scheduleId, date, date.PlusDays(1), ct).ConfigureAwait(false);

        if (!open.Any(s => s.StartUtc == startUtc))
        {
            return new AppointmentResult(null, "Bu saat artık uygun değil. Lütfen başka bir saat seçin.");
        }

        var startLocal = zones.ToLocal(startUtc, zoneId);
        var endLocal = startLocal.PlusMinutes(schedule.DurationMinutes);

        var title = $"{schedule.Name} — {guestName.Trim()}";

        var input = new EventInput
        {
            CalendarId = schedule.CalendarId,
            Title = title,
            DescriptionHtml = BuildDescription(guestName, guestEmail, note),
            LocationText = schedule.LocationText,
            OnlineMeetingUrl = schedule.OnlineMeetingProvider == "jitsi"
                ? MeetingLinks.CreateJitsiUrl(title)
                : null,
            OnlineMeetingProvider = schedule.OnlineMeetingProvider,
            StartLocal = startLocal,
            EndLocal = endLocal,
            StartTimeZoneId = zoneId,
            EndTimeZoneId = zoneId,
            Availability = Availability.Busy,
            ActorUserId = schedule.OwnerUserId,
        };

        var created = await events.CreateAsync(input, ct).ConfigureAwait(false);

        var appointment = new Appointment
        {
            ScheduleId = scheduleId,
            EventId = created.PrimaryEventId,
            BookedByUserId = bookedByUserId,
            GuestName = guestName.Trim(),
            GuestEmail = string.IsNullOrWhiteSpace(guestEmail) ? null : guestEmail.Trim(),
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            StartUtc = startUtc,
            EndUtc = zones.ToInstant(endLocal, zoneId),
            CreatedAt = clock.GetCurrentInstant().ToDateTimeOffset(),
        };

        db.Appointments.Add(appointment);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new AppointmentResult(appointment, null);
    }

    /// <summary>Bir sayfaya alınmış randevular. İptal edilenler istenirse dahil edilir.</summary>
    public Task<List<Appointment>> GetAppointmentsAsync(
        Guid scheduleId, bool includeCancelled = false, CancellationToken ct = default)
        => db.Appointments
            .AsNoTracking()
            .Where(a => a.ScheduleId == scheduleId && (includeCancelled || a.CancelledAt == null))
            .OrderBy(a => a.StartUtc)
            .ToListAsync(ct);

    /// <summary>Kullanıcının tüm sayfalarına gelen yaklaşan randevular.</summary>
    public Task<List<Appointment>> GetUpcomingAsync(Guid ownerUserId, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();

        return db.Appointments
            .AsNoTracking()
            .Include(a => a.Schedule)
            .Where(a => a.Schedule!.OwnerUserId == ownerUserId
                        && a.CancelledAt == null
                        && a.EndUtc > now)
            .OrderBy(a => a.StartUtc)
            .ToListAsync(ct);
    }

    /// <summary>Randevuyu iptal eder; takvimdeki etkinlik de silinir, saat serbest kalır.</summary>
    public async Task CancelAsync(
        Guid appointmentId, string? reason, Guid actorUserId, CancellationToken ct = default)
    {
        var appointment = await db.Appointments
            .FirstOrDefaultAsync(a => a.Id == appointmentId, ct).ConfigureAwait(false);

        if (appointment is null || appointment.IsCancelled) return;

        appointment.CancelledAt = clock.GetCurrentInstant().ToDateTimeOffset();
        appointment.CancellationReason = reason;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await events.DeleteAsync(appointment.EventId, null, SeriesEditScope.AllInSeries, actorUserId, ct)
            .ConfigureAwait(false);
    }

    // ==================================================================

    private static void ApplyInput(AppointmentSchedule schedule, AppointmentScheduleInput input)
    {
        schedule.Description = input.Description;
        schedule.CalendarId = input.CalendarId;
        schedule.DurationMinutes = Math.Clamp(input.DurationMinutes, 5, 8 * 60);
        schedule.BufferMinutes = Math.Clamp(input.BufferMinutes, 0, 120);
        schedule.MinimumNoticeHours = Math.Clamp(input.MinimumNoticeHours, 0, 30 * 24);
        schedule.MaximumAdvanceDays = Math.Clamp(input.MaximumAdvanceDays, 1, 365);
        schedule.MaximumPerDay = input.MaximumPerDay is { } cap && cap > 0 ? cap : null;
        schedule.LocationText = input.LocationText;
        schedule.OnlineMeetingProvider = input.OnlineMeetingProvider;
    }

    /// <summary>Girdideki haftalık pencereleri kayda çevirir.</summary>
    private static List<AppointmentWindow> BuildWindows(Guid scheduleId, AppointmentScheduleInput input)
    {
        var windows = new List<AppointmentWindow>(input.Windows.Count);

        foreach (var (day, start, end) in input.Windows)
        {
            // Ters ya da sıfır uzunluklu pencere dilim üretmez; sessizce atılır.
            if (end <= start) continue;

            windows.Add(new AppointmentWindow
            {
                ScheduleId = scheduleId,
                DayOfWeek = day,
                Start = start,
                End = end,
            });
        }

        return windows;
    }

    /// <summary>Bir pencereyi dilimlere böler.</summary>
    private IEnumerable<AppointmentSlot> SlotsIn(
        AppointmentSchedule schedule, LocalDate date, AppointmentWindow window, string zoneId)
    {
        var cursor = date.At(window.Start);
        var limit = date.At(window.End);

        while (cursor.PlusMinutes(schedule.DurationMinutes) <= limit)
        {
            var startUtc = zones.ToInstant(cursor, zoneId);
            var endUtc = zones.ToInstant(cursor.PlusMinutes(schedule.DurationMinutes), zoneId);

            yield return new AppointmentSlot(startUtc, endUtc, cursor);

            // Bir sonraki dilim, süre artı tampon kadar sonra başlar.
            cursor = cursor.PlusMinutes(schedule.DurationMinutes + schedule.BufferMinutes);
        }
    }

    /// <summary>
    /// Sahibin meşgul aralıkları. Sayfanın takvimi değil <b>sahibin tümü</b>
    /// okunur: başka bir takvimdeki toplantı da o saatte müsait olmadığı
    /// anlamına gelir.
    /// </summary>
    private async Task<List<(Instant Start, Instant End)>> LoadBusyAsync(
        AppointmentSchedule schedule, Instant from, Instant to, CancellationToken ct)
    {
        var occurrences = await query
            .GetOccurrencesAsync(schedule.OwnerUserId, from, to, ct: ct)
            .ConfigureAwait(false);

        return [.. occurrences
            .Where(o => o.Source.Availability != Availability.Free)
            .Select(o => (o.StartUtc, o.EndUtc))];
    }

    /// <summary>Günlük sınır için, gün başına alınmış randevu sayısı.</summary>
    private async Task<Dictionary<LocalDate, int>> CountPerDayAsync(
        Guid scheduleId, Instant from, Instant to, string zoneId, CancellationToken ct)
    {
        var taken = await db.Appointments
            .AsNoTracking()
            .Where(a => a.ScheduleId == scheduleId
                        && a.CancelledAt == null
                        && a.StartUtc >= from
                        && a.StartUtc < to)
            .Select(a => a.StartUtc)
            .ToListAsync(ct).ConfigureAwait(false);

        var counts = new Dictionary<LocalDate, int>();

        foreach (var start in taken)
        {
            var date = zones.ToLocal(start, zoneId).Date;
            counts[date] = counts.GetValueOrDefault(date) + 1;
        }

        return counts;
    }

    private static bool Overlaps(List<(Instant Start, Instant End)> busy, AppointmentSlot slot)
        => busy.Exists(b => b.Start < slot.EndUtc && b.End > slot.StartUtc);

    private static string BuildDescription(string guestName, string? guestEmail, string? note)
    {
        var lines = new List<string> { $"Randevu: {guestName.Trim()}" };

        if (!string.IsNullOrWhiteSpace(guestEmail)) lines.Add(guestEmail.Trim());

        if (!string.IsNullOrWhiteSpace(note))
        {
            lines.Add(string.Empty);
            lines.Add(note.Trim());
        }

        return string.Join('\n', lines);
    }

    /// <summary>Adres parçasını benzersiz kılar; çakışırsa sonuna sayı eklenir.</summary>
    private async Task<string> UniqueSlugAsync(string source, Guid? ignoreId, CancellationToken ct)
    {
        var baseSlug = Slugify(source);
        var candidate = baseSlug;
        var suffix = 2;

        while (await db.AppointmentSchedules
            .AnyAsync(s => s.Slug == candidate && (ignoreId == null || s.Id != ignoreId), ct)
            .ConfigureAwait(false))
        {
            candidate = $"{baseSlug}-{suffix++}";
        }

        return candidate;
    }

    private static string Slugify(string source)
    {
        var normalized = TurkishText.Normalize(source);
        var builder = new System.Text.StringBuilder(normalized.Length);

        foreach (var ch in normalized)
        {
            if (char.IsAsciiLetterOrDigit(ch)) builder.Append(ch);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length > 40) slug = slug[..40].TrimEnd('-');

        return slug.Length == 0 ? "randevu" : slug;
    }
}
