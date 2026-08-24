using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Recurrence;
using Takvim.Core.Text;
using Takvim.Core.Time;

namespace Takvim.Data.Services;

/// <summary>
/// Etkinlik yazma işlemlerinin tamamı. Türetilmiş alanların hesaplanması,
/// tekrarlayan serilerde kapsam kuralları, değişiklik günlüğü ve çöp kutusu
/// tek noktadan yönetilir; arayüz doğrudan <see cref="Event"/> satırı yazmaz.
/// </summary>
public sealed class EventService(
    TakvimDbContext db,
    TimeZoneService timeZones,
    RecurrenceExpander expander,
    IClock clock)
{
    private readonly ChangeLogWriter _log = new(db);

    /// <summary>Silinen etkinliklerin çöp kutusunda kalma süresi.</summary>
    public static readonly Duration TrashRetention = Duration.FromDays(30);

    // ==================================================================
    // Oluşturma
    // ==================================================================

    public async Task<EventWriteResult> CreateAsync(EventInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var operationId = Guid.NewGuid();
        var ev = new Event
        {
            Uid = $"{Guid.NewGuid():N}@takvim.local",
            ETag = NewETag(),
            OrganizerUserId = input.ActorUserId,
        };

        ApplyInput(ev, input);
        db.Events.Add(ev);
        SyncChildren(ev, input);

        _log.RecordEvent(operationId, ChangeOperation.Create, ev.Id, ev.CalendarId,
            before: null, after: EventSnapshot.From(ev), input.ActorUserId,
            summary: $"\"{ev.Title}\" oluşturuldu");

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new EventWriteResult(ev.Id, operationId, "Etkinlik oluşturuldu");
    }

    // ==================================================================
    // Düzenleme
    // ==================================================================

    /// <summary>
    /// Etkinliği günceller. Tekrarlayan bir seride <paramref name="scope"/> hangi
    /// örneklerin etkileneceğini belirler; <paramref name="recurrenceId"/> ise
    /// düzenlemenin hangi örnekten başladığını söyler.
    /// </summary>
    public async Task<EventWriteResult> UpdateAsync(
        Guid eventId,
        LocalDateTime? recurrenceId,
        SeriesEditScope scope,
        EventInput input,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var target = await LoadWithChildrenAsync(eventId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Etkinlik bulunamadı: {eventId}");

        var root = await ResolveRootAsync(target, ct).ConfigureAwait(false);
        var operationId = Guid.NewGuid();

        // Tekrarlamayan etkinlikte kapsamın anlamı yoktur.
        if (string.IsNullOrWhiteSpace(root.RecurrenceRule))
        {
            UpdateSingle(operationId, root, input);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new EventWriteResult(root.Id, operationId, "Etkinlik güncellendi");
        }

        var result = scope switch
        {
            SeriesEditScope.ThisOnly => await UpdateThisOnlyAsync(operationId, root, recurrenceId, input, ct).ConfigureAwait(false),
            SeriesEditScope.ThisAndFuture => await UpdateThisAndFutureAsync(operationId, root, recurrenceId, input, ct).ConfigureAwait(false),
            SeriesEditScope.AllInSeries => await UpdateAllInSeriesAsync(operationId, root, input, ct).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return result;
    }

    private void UpdateSingle(Guid operationId, Event ev, EventInput input)
    {
        var before = EventSnapshot.From(ev);
        ApplyInput(ev, input);
        SyncChildren(ev, input);

        _log.RecordEvent(operationId, ChangeOperation.Update, ev.Id, ev.CalendarId,
            before, EventSnapshot.From(ev), input.ActorUserId,
            summary: DescribeChange(before, ev));
    }

    /// <summary>Tek örneği seriden sapmış bir istisna satırına dönüştürür.</summary>
    private async Task<EventWriteResult> UpdateThisOnlyAsync(
        Guid operationId, Event root, LocalDateTime? recurrenceId, EventInput input, CancellationToken ct)
    {
        if (recurrenceId is not { } rid)
            throw new ArgumentNullException(nameof(recurrenceId), "Tek örnek düzenlemesi için örnek kimliği gerekir.");

        var exception = await db.Events
            .Include(e => e.Reminders)
            .Include(e => e.Categories)
            .FirstOrDefaultAsync(e => e.SeriesId == root.Id && e.RecurrenceId == rid, ct)
            .ConfigureAwait(false);

        if (exception is null)
        {
            exception = new Event
            {
                Uid = root.Uid,               // RFC 5545: istisna, seriyle aynı UID'yi taşır
                ETag = NewETag(),
                SeriesId = root.Id,
                RecurrenceId = rid,
                OrganizerUserId = root.OrganizerUserId,
            };
            db.Events.Add(exception);

            ApplyInput(exception, input with { RecurrenceRule = null });
            SyncChildren(exception, input);

            _log.RecordEvent(operationId, ChangeOperation.Create, exception.Id, exception.CalendarId,
                before: null, after: EventSnapshot.From(exception), input.ActorUserId,
                summary: $"\"{root.Title}\" serisinin bir örneği düzenlendi");
        }
        else
        {
            var before = EventSnapshot.From(exception);
            ApplyInput(exception, input with { RecurrenceRule = null });
            SyncChildren(exception, input);

            _log.RecordEvent(operationId, ChangeOperation.Update, exception.Id, exception.CalendarId,
                before, EventSnapshot.From(exception), input.ActorUserId,
                summary: DescribeChange(before, exception));
        }

        return new EventWriteResult(exception.Id, operationId, "Bu örnek güncellendi");
    }

    /// <summary>
    /// Seriyi ikiye böler: özgün seri bu örnekten önce sonlandırılır, kalanı
    /// yeni bir seri olur. Sonraki istisnalar ve çıkarılmış tarihler yeni seriye taşınır.
    /// </summary>
    private async Task<EventWriteResult> UpdateThisAndFutureAsync(
        Guid operationId, Event root, LocalDateTime? recurrenceId, EventInput input, CancellationToken ct)
    {
        if (recurrenceId is not { } splitAt)
            throw new ArgumentNullException(nameof(recurrenceId), "Bölme için örnek kimliği gerekir.");

        // Bölünme noktası serinin ilk örneğiyse, bölmeye gerek yok: tüm seri güncellenir.
        if (splitAt <= root.StartLocal)
            return await UpdateAllInSeriesAsync(operationId, root, input, ct).ConfigureAwait(false);

        var splitUtc = timeZones.ToInstant(splitAt, root.StartTimeZoneId ?? root.Calendar?.TimeZoneId);
        var rootBefore = EventSnapshot.From(root);

        var exceptions = await db.Events
            .Include(e => e.Reminders)
            .Include(e => e.Categories)
            .Where(e => e.SeriesId == root.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        // 1) Özgün seriyi kes. UNTIL bir saniye öncesine konur ki bölünme
        //    noktasındaki örnek eski seride kalmasın.
        var remainingCount = CountOccurrencesBefore(root, splitUtc);
        root.RecurrenceRule = RecurrenceRuleBuilder.TruncateBefore(
            root.RecurrenceRule!, splitUtc - Duration.FromSeconds(1));
        root.ExDates = FilterDates(root.ExDates, keep: d => d < splitAt);
        Touch(root);
        root.SeriesEndUtc = expander.CalculateSeriesEnd(root);

        _log.RecordEvent(operationId, ChangeOperation.Update, root.Id, root.CalendarId,
            rootBefore, EventSnapshot.From(root), input.ActorUserId,
            summary: $"\"{root.Title}\" serisi {splitAt:dd.MM.yyyy} tarihinde bölündü");

        // 2) Kalanı yeni bir seri olarak aç. Bu ayrı bir seridir; yeni UID alır.
        var newSeries = new Event
        {
            Uid = $"{Guid.NewGuid():N}@takvim.local",
            ETag = NewETag(),
            OrganizerUserId = root.OrganizerUserId,
            RDates = FilterDates(root.RDates, keep: d => d >= splitAt),
        };
        db.Events.Add(newSeries);

        var newRule = input.RecurrenceRule ?? root.RecurrenceRule;
        ApplyInput(newSeries, input with { RecurrenceRule = AdjustCount(newRule, remainingCount) });
        SyncChildren(newSeries, input);
        newSeries.ExDates = FilterDates(rootBefore.ExDates, keep: d => d >= splitAt);
        newSeries.SeriesEndUtc = expander.CalculateSeriesEnd(newSeries);

        _log.RecordEvent(operationId, ChangeOperation.Create, newSeries.Id, newSeries.CalendarId,
            before: null, after: EventSnapshot.From(newSeries), input.ActorUserId,
            summary: $"\"{newSeries.Title}\" serisi {splitAt:dd.MM.yyyy} tarihinden itibaren açıldı");

        // 3) Bölünme noktasından sonraki istisnaları yeni seriye bağla.
        foreach (var exception in exceptions.Where(e => e.RecurrenceId >= splitAt))
        {
            var before = EventSnapshot.From(exception);
            exception.SeriesId = newSeries.Id;
            exception.Uid = newSeries.Uid;
            Touch(exception);

            _log.RecordEvent(operationId, ChangeOperation.Update, exception.Id, exception.CalendarId,
                before, EventSnapshot.From(exception), input.ActorUserId,
                summary: "İstisna yeni seriye taşındı");
        }

        return new EventWriteResult(newSeries.Id, operationId, "Bu ve sonraki etkinlikler güncellendi");
    }

    /// <summary>
    /// Tüm seriyi günceller. Tek tek taşınmış/değiştirilmiş örnekler sıfırlanır;
    /// silinmiş örnekler (EXDATE) korunur. Bu davranış arayüzde açıkça belirtilir.
    /// </summary>
    private async Task<EventWriteResult> UpdateAllInSeriesAsync(
        Guid operationId, Event root, EventInput input, CancellationToken ct)
    {
        var before = EventSnapshot.From(root);
        var exDates = root.ExDates;   // silinmiş örnekler korunur

        ApplyInput(root, input);
        SyncChildren(root, input);
        root.ExDates = exDates;
        root.SeriesEndUtc = expander.CalculateSeriesEnd(root);

        _log.RecordEvent(operationId, ChangeOperation.Update, root.Id, root.CalendarId,
            before, EventSnapshot.From(root), input.ActorUserId,
            summary: $"\"{root.Title}\" serisinin tamamı güncellendi");

        // Sapmış örnekler sıfırlanır: seri genelinde yapılan değişiklikle
        // çelişmemeleri için silinirler ve seriden yeniden üretilirler.
        var exceptions = await db.Events
            .Where(e => e.SeriesId == root.Id && e.Status != EventStatus.Cancelled)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var exception in exceptions)
        {
            _log.RecordEvent(operationId, ChangeOperation.Delete, exception.Id, exception.CalendarId,
                EventSnapshot.From(exception), after: null, input.ActorUserId,
                summary: "Seri genelinde düzenleme nedeniyle örnek sıfırlandı");
            db.Events.Remove(exception);
        }

        return new EventWriteResult(root.Id, operationId, "Tüm seri güncellendi");
    }

    // ==================================================================
    // Silme ve çöp kutusu
    // ==================================================================

    public async Task<EventWriteResult> DeleteAsync(
        Guid eventId,
        LocalDateTime? recurrenceId,
        SeriesEditScope scope,
        Guid? actorUserId = null,
        CancellationToken ct = default)
    {
        var target = await LoadWithChildrenAsync(eventId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Etkinlik bulunamadı: {eventId}");

        var root = await ResolveRootAsync(target, ct).ConfigureAwait(false);
        var operationId = Guid.NewGuid();

        if (string.IsNullOrWhiteSpace(root.RecurrenceRule))
        {
            SoftDelete(operationId, root, actorUserId, $"\"{root.Title}\" silindi");
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new EventWriteResult(root.Id, operationId, "Etkinlik silindi");
        }

        var result = scope switch
        {
            SeriesEditScope.ThisOnly => await DeleteThisOnlyAsync(operationId, root, recurrenceId, actorUserId, ct).ConfigureAwait(false),
            SeriesEditScope.ThisAndFuture => await DeleteThisAndFutureAsync(operationId, root, recurrenceId, actorUserId, ct).ConfigureAwait(false),
            SeriesEditScope.AllInSeries => await DeleteAllInSeriesAsync(operationId, root, actorUserId, ct).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(scope)),
        };

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Tek örneği siler: seri köküne EXDATE eklenir. Bu biçim ICS ve CalDAV'a
    /// doğrudan taşınabilir olduğu için iptal satırı yerine tercih edilir.
    /// </summary>
    private async Task<EventWriteResult> DeleteThisOnlyAsync(
        Guid operationId, Event root, LocalDateTime? recurrenceId, Guid? actorUserId, CancellationToken ct)
    {
        if (recurrenceId is not { } rid)
            throw new ArgumentNullException(nameof(recurrenceId), "Tek örnek silmek için örnek kimliği gerekir.");

        var before = EventSnapshot.From(root);
        var exDates = RecurrenceExpander.ParseDateList(root.ExDates);
        exDates.Add(rid);
        root.ExDates = RecurrenceExpander.FormatDateList(exDates);
        Touch(root);

        _log.RecordEvent(operationId, ChangeOperation.Update, root.Id, root.CalendarId,
            before, EventSnapshot.From(root), actorUserId,
            summary: $"\"{root.Title}\" serisinden {rid:dd.MM.yyyy HH:mm} örneği silindi");

        // O örneğe ait sapmış satır varsa artık anlamsızdır.
        var exception = await db.Events
            .FirstOrDefaultAsync(e => e.SeriesId == root.Id && e.RecurrenceId == rid, ct)
            .ConfigureAwait(false);

        if (exception is not null)
        {
            _log.RecordEvent(operationId, ChangeOperation.Delete, exception.Id, exception.CalendarId,
                EventSnapshot.From(exception), after: null, actorUserId,
                summary: "Silinen örneğin istisna satırı kaldırıldı");
            db.Events.Remove(exception);
        }

        return new EventWriteResult(root.Id, operationId, "Bu etkinlik silindi");
    }

    private async Task<EventWriteResult> DeleteThisAndFutureAsync(
        Guid operationId, Event root, LocalDateTime? recurrenceId, Guid? actorUserId, CancellationToken ct)
    {
        if (recurrenceId is not { } splitAt)
            throw new ArgumentNullException(nameof(recurrenceId), "Bölme için örnek kimliği gerekir.");

        // İlk örnekten itibaren siliniyorsa seri tümüyle gider.
        if (splitAt <= root.StartLocal)
            return await DeleteAllInSeriesAsync(operationId, root, actorUserId, ct).ConfigureAwait(false);

        var splitUtc = timeZones.ToInstant(splitAt, root.StartTimeZoneId ?? root.Calendar?.TimeZoneId);
        var before = EventSnapshot.From(root);

        root.RecurrenceRule = RecurrenceRuleBuilder.TruncateBefore(
            root.RecurrenceRule!, splitUtc - Duration.FromSeconds(1));
        Touch(root);
        root.SeriesEndUtc = expander.CalculateSeriesEnd(root);

        _log.RecordEvent(operationId, ChangeOperation.Update, root.Id, root.CalendarId,
            before, EventSnapshot.From(root), actorUserId,
            summary: $"\"{root.Title}\" serisi {splitAt:dd.MM.yyyy} tarihinden itibaren silindi");

        var laterExceptions = await db.Events
            .Include(e => e.Reminders)
            .Where(e => e.SeriesId == root.Id && e.RecurrenceId >= splitAt)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var exception in laterExceptions)
            SoftDelete(operationId, exception, actorUserId, "Seri kesildiği için örnek silindi");

        return new EventWriteResult(root.Id, operationId, "Bu ve sonraki etkinlikler silindi");
    }

    private async Task<EventWriteResult> DeleteAllInSeriesAsync(
        Guid operationId, Event root, Guid? actorUserId, CancellationToken ct)
    {
        SoftDelete(operationId, root, actorUserId, $"\"{root.Title}\" serisinin tamamı silindi");

        var exceptions = await db.Events
            .Include(e => e.Reminders)
            .Where(e => e.SeriesId == root.Id && e.DeletedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var exception in exceptions)
            SoftDelete(operationId, exception, actorUserId, "Seri silindiği için örnek silindi");

        return new EventWriteResult(root.Id, operationId, "Tüm seri silindi");
    }

    private void SoftDelete(Guid operationId, Event ev, Guid? actorUserId, string summary)
    {
        var before = EventSnapshot.From(ev);
        ev.DeletedAt = clock.GetCurrentInstant().ToDateTimeOffset();
        Touch(ev);

        _log.RecordEvent(operationId, ChangeOperation.Delete, ev.Id, ev.CalendarId,
            before, EventSnapshot.From(ev), actorUserId, summary);
    }

    /// <summary>
    /// Toplantıyı iptal eder. Silmekten farklıdır: kayıt durur, katılımcıların
    /// takviminde üstü çizili görünür ve gerekçe okunabilir. Silmek bu bilgiyi
    /// yok ederdi — davetliler toplantının neden olmadığını bilemezdi.
    /// </summary>
    public async Task<EventWriteResult> CancelMeetingAsync(
        Guid eventId, string? reason, Guid? actorUserId = null, CancellationToken ct = default)
    {
        var ev = await db.Events.FirstOrDefaultAsync(e => e.Id == eventId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Etkinlik bulunamadı: {eventId}");

        var operationId = Guid.NewGuid();
        var before = EventSnapshot.From(ev);

        ev.Status = EventStatus.Cancelled;
        ev.CancellationReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        Touch(ev);

        _log.RecordEvent(operationId, ChangeOperation.Update, ev.Id, ev.CalendarId,
            before, EventSnapshot.From(ev), actorUserId,
            summary: $"\"{ev.Title}\" iptal edildi" +
                     (ev.CancellationReason is null ? "" : $": {ev.CancellationReason}"));

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new EventWriteResult(ev.Id, operationId, "Toplantı iptal edildi");
    }

    /// <summary>Çöp kutusundaki etkinliği geri getirir.</summary>
    public async Task<EventWriteResult> RestoreAsync(Guid eventId, Guid? actorUserId = null, CancellationToken ct = default)
    {
        var ev = await db.Events.FirstOrDefaultAsync(e => e.Id == eventId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Etkinlik bulunamadı: {eventId}");

        var operationId = Guid.NewGuid();
        var before = EventSnapshot.From(ev);
        ev.DeletedAt = null;
        Touch(ev);

        _log.RecordEvent(operationId, ChangeOperation.Restore, ev.Id, ev.CalendarId,
            before, EventSnapshot.From(ev), actorUserId, $"\"{ev.Title}\" çöp kutusundan geri alındı");

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new EventWriteResult(ev.Id, operationId, "Etkinlik geri alındı");
    }

    /// <summary>Saklama süresi dolmuş çöp kutusu kayıtlarını kalıcı olarak siler.</summary>
    public async Task<int> PurgeTrashAsync(CancellationToken ct = default)
    {
        var cutoff = clock.GetCurrentInstant().Minus(TrashRetention).ToDateTimeOffset();

        return await db.Events
            .Where(e => e.DeletedAt != null && e.DeletedAt < cutoff)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    // ==================================================================
    // Ortak yardımcılar
    // ==================================================================

    /// <summary>Girdiyi satıra uygular ve türetilmiş alanların tamamını yeniden hesaplar.</summary>
    private void ApplyInput(Event ev, EventInput input)
    {
        ev.CalendarId = input.CalendarId;
        ev.Title = input.Title;
        ev.DescriptionHtml = input.DescriptionHtml;
        ev.LocationText = input.LocationText;
        ev.OnlineMeetingUrl = input.OnlineMeetingUrl;
        ev.OnlineMeetingProvider = input.OnlineMeetingProvider;
        ev.Color = input.Color;

        ev.IsAllDay = input.IsAllDay;
        ev.StartLocal = input.StartLocal;
        ev.EndLocal = input.EndLocal;
        ev.StartTimeZoneId = input.IsAllDay ? null : input.StartTimeZoneId;
        ev.EndTimeZoneId = input.IsAllDay ? null : (input.EndTimeZoneId ?? input.StartTimeZoneId);

        ev.Availability = input.Availability;
        ev.Visibility = input.Visibility;
        ev.IsForwardable = input.IsForwardable;
        ev.RecurrenceRule = string.IsNullOrWhiteSpace(input.RecurrenceRule) ? null : input.RecurrenceRule;
        ev.HolidayBehavior = input.HolidayBehavior;

        RecalculateDerived(ev);
        Touch(ev);

        if (ev.RecurrenceRule is not null)
            ev.SeriesEndUtc = expander.CalculateSeriesEnd(ev);
        else
            ev.SeriesEndUtc = ev.EndUtc;
    }

    /// <summary>UTC önbelleği ve arama metni gibi türetilmiş alanları yeniler.</summary>
    public void RecalculateDerived(Event ev)
    {
        ArgumentNullException.ThrowIfNull(ev);

        // Tüm gün etkinliklerinin dilimi yoktur; UTC önbelleği takvimin diliminden hesaplanır.
        var startZone = ev.StartTimeZoneId ?? ev.Calendar?.TimeZoneId ?? TimeZoneService.DefaultZoneId;
        var endZone = ev.EndTimeZoneId ?? startZone;

        ev.StartUtc = timeZones.ToInstant(ev.StartLocal, startZone);
        ev.EndUtc = timeZones.ToInstant(ev.EndLocal, endZone);

        ev.SearchText = TurkishText.BuildSearchText(ev.Title, ev.DescriptionHtml, ev.LocationText);
    }

    private void Touch(Event ev)
    {
        ev.Sequence++;
        ev.ETag = NewETag();
        ev.LastModifiedUtc = clock.GetCurrentInstant();
        ev.UpdatedAt = ev.LastModifiedUtc.ToDateTimeOffset();
    }

    private void SyncChildren(Event ev, EventInput input)
    {
        db.Reminders.RemoveRange(ev.Reminders);
        ev.Reminders = [.. input.Reminders.Select(r => new Reminder
        {
            EventId = ev.Id,
            MinutesBefore = r.MinutesBefore,
            Channel = r.Channel,
        })];

        db.EventCategories.RemoveRange(ev.Categories);
        ev.Categories = [.. input.CategoryIds.Distinct().Select(id => new EventCategory
        {
            EventId = ev.Id,
            CategoryId = id,
        })];
    }

    private Task<Event?> LoadWithChildrenAsync(Guid eventId, CancellationToken ct)
        => db.Events
            .Include(e => e.Reminders)
            .Include(e => e.Categories)
            .Include(e => e.Calendar)
            .FirstOrDefaultAsync(e => e.Id == eventId, ct);

    /// <summary>İstisna satırından seri köküne çıkar. Kök zaten verilmişse kendisini döner.</summary>
    private async Task<Event> ResolveRootAsync(Event target, CancellationToken ct)
    {
        if (target.SeriesId is not { } seriesId) return target;

        return await db.Events
            .Include(e => e.Reminders)
            .Include(e => e.Categories)
            .Include(e => e.Calendar)
            .FirstOrDefaultAsync(e => e.Id == seriesId, ct)
            .ConfigureAwait(false) ?? target;
    }

    /// <summary>Bölünme noktasına kadar üretilen örnek sayısı; COUNT'lu serilerde kalanı bulmak için.</summary>
    private int CountOccurrencesBefore(Event root, Instant splitUtc)
        => expander.Expand(root, null, root.StartUtc, splitUtc).Count();

    /// <summary>COUNT içeren bir kuralda kalan tekrar sayısını düşürür.</summary>
    private static string? AdjustCount(string? rrule, int consumed)
    {
        if (string.IsNullOrWhiteSpace(rrule)) return rrule;

        var pattern = new Ical.Net.DataTypes.RecurrencePattern(rrule);
        if (pattern.Count is not { } total) return rrule;

        pattern.Count = Math.Max(1, total - consumed);

        var normalized = pattern.ToString();
        return string.IsNullOrWhiteSpace(normalized) ? rrule : RecurrenceRuleBuilder.Normalize(normalized);
    }

    private static string? FilterDates(string? list, Func<LocalDateTime, bool> keep)
    {
        var dates = RecurrenceExpander.ParseDateList(list).Where(keep).ToList();
        return dates.Count == 0 ? null : RecurrenceExpander.FormatDateList(dates);
    }

    /// <summary>Denetim kaydında görünecek insan okunur özet.</summary>
    private static string DescribeChange(EventSnapshot before, Event after)
    {
        var changes = new List<string>();

        if (before.Title != after.Title)
            changes.Add($"başlık \"{before.Title}\" -> \"{after.Title}\"");
        if (before.StartLocal != after.StartLocal.ToString("uuuu-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))
            changes.Add($"başlangıç {after.StartLocal:dd.MM.yyyy HH:mm} olarak değişti");
        if (before.CalendarId != after.CalendarId)
            changes.Add("başka takvime taşındı");
        if (before.LocationText != after.LocationText)
            changes.Add("konum değişti");

        return changes.Count == 0
            ? $"\"{after.Title}\" güncellendi"
            : $"\"{after.Title}\": " + string.Join(", ", changes);
    }

    private static string NewETag() => Guid.NewGuid().ToString("N")[..16];
}
