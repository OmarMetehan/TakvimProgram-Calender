using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Ics;
using Takvim.Core.Permissions;

// System.Globalization.Calendar ile çakışır; burada "Calendar" daima takvimimizdir.
using Calendar = Takvim.Core.Domain.Calendar;

namespace Takvim.Data.Services;

/// <summary>CalDAV istemcisine gösterilen bir takvim koleksiyonu.</summary>
/// <param name="Calendar">Takvim kaydı.</param>
/// <param name="IsReadOnly">İstemci bu koleksiyona yazabilir mi.</param>
/// <param name="CTag">Koleksiyon etiketi; içerik değiştikçe değişir.</param>
public sealed record CalDavCalendarInfo(Calendar Calendar, bool IsReadOnly, string CTag);

/// <summary>Koleksiyondaki tek bir kaynak: bir UID'ye ait tüm satırlar.</summary>
/// <param name="Uid">iCalendar UID; dosya adının gövdesidir.</param>
/// <param name="ETag">Kaynağın sürüm etiketi.</param>
/// <param name="LastModified">En son değişiklik anı.</param>
public sealed record CalDavResourceInfo(string Uid, string ETag, Instant LastModified);

/// <summary>Artımlı senkronizasyonun sonucu.</summary>
/// <param name="Changed">Değişen ya da eklenen kaynaklar.</param>
/// <param name="Removed">Silinen kaynakların UID'leri.</param>
/// <param name="SyncToken">İstemcinin bir dahaki sefere göndereceği imleç.</param>
public sealed record CalDavSyncResult(
    IReadOnlyList<CalDavResourceInfo> Changed,
    IReadOnlyList<string> Removed,
    long SyncToken);

/// <summary>Yazma işleminin sonucu.</summary>
public enum CalDavWriteOutcome
{
    Created,
    Updated,
    Deleted,
    /// <summary>If-Match uyuşmadı; istemcinin elindeki sürüm eski.</summary>
    Conflict,
    /// <summary>Koleksiyon salt okunur ya da erişim yok.</summary>
    Forbidden,
    NotFound,
    /// <summary>Gönderilen ICS okunamadı.</summary>
    Invalid,
}

public sealed record CalDavWriteResult(CalDavWriteOutcome Outcome, string? ETag = null, string? Message = null);

/// <summary>
/// CalDAV sunucusunun veri katmanı.
/// <para>
/// Bir CalDAV <b>kaynağı</b>, bir UID'ye ait <b>tüm</b> satırlardır: seri kökü ve
/// RECURRENCE-ID taşıyan istisnaları tek bir <c>.ics</c> dosyasında birlikte
/// bulunur. Veri modelimiz bunları ayrı satırlarda tuttuğu için okuma birleştirir,
/// yazma da ayrıştırır. Etiketler (ETag) kaynağın tamamını kapsamak zorundadır;
/// yalnızca kökün etiketi kullanılsaydı istisna değişince istemci fark etmezdi.
/// </para>
/// </summary>
public sealed class CalDavStore(
    TakvimDbContext db,
    CalendarPermissions permissions,
    IcsSerializer ics,
    IClock clock)
{
    /// <summary>Kullanıcının CalDAV üzerinden göreceği takvimler.</summary>
    public async Task<List<CalDavCalendarInfo>> GetCollectionsAsync(Guid userId, CancellationToken ct = default)
    {
        var scope = await permissions.LoadAsync(userId, ct).ConfigureAwait(false);
        var ids = scope.ReadableCalendarIds.ToList();

        var calendars = await db.Calendars
            .AsNoTracking()
            .Where(c => ids.Contains(c.Id) && c.DeletedAt == null)
            .OrderBy(c => c.OwnerUserId == userId ? 0 : 1)
            .ThenBy(c => c.SortOrder)
            .ToListAsync(ct).ConfigureAwait(false);

        var result = new List<CalDavCalendarInfo>(calendars.Count);

        foreach (var calendar in calendars)
        {
            // Paylaşılan takvimlerde yazma yetkisi yoksa koleksiyon salt okunurdur.
            var readOnly = calendar.IsReadOnly || !scope.CanWriteTo(calendar.Id);
            result.Add(new CalDavCalendarInfo(calendar, readOnly, await CTagAsync(calendar.Id, ct).ConfigureAwait(false)));
        }

        return result;
    }

    public async Task<CalDavCalendarInfo?> GetCollectionAsync(
        Guid userId, Guid calendarId, CancellationToken ct = default)
    {
        var collections = await GetCollectionsAsync(userId, ct).ConfigureAwait(false);
        return collections.FirstOrDefault(c => c.Calendar.Id == calendarId);
    }

    /// <summary>
    /// Koleksiyon etiketi. İstemci bunu saklar; değişmediyse hiçbir şeyi
    /// yeniden çekmez. Değişiklik günlüğündeki son imleçten türetilir.
    /// </summary>
    public async Task<string> CTagAsync(Guid calendarId, CancellationToken ct = default)
    {
        var last = await db.ChangeLog
            .AsNoTracking()
            .Where(c => c.CalendarId == calendarId)
            .OrderByDescending(c => c.SyncToken)
            .Select(c => (long?)c.SyncToken)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return (last ?? 0).ToString(CultureInfo.InvariantCulture);
    }

    // ==================================================================
    // Okuma
    // ==================================================================

    /// <summary>Koleksiyondaki tüm kaynakları etiketleriyle listeler.</summary>
    public async Task<List<CalDavResourceInfo>> ListResourcesAsync(
        Guid calendarId, CancellationToken ct = default)
    {
        var rows = await db.Events
            .AsNoTracking()
            .Where(e => e.CalendarId == calendarId && e.DeletedAt == null)
            .Select(e => new { e.Uid, e.ETag, e.LastModifiedUtc })
            .ToListAsync(ct).ConfigureAwait(false);

        return
        [
            .. rows
                .GroupBy(r => r.Uid, StringComparer.Ordinal)
                .Select(g => new CalDavResourceInfo(
                    g.Key,
                    ComposeETag(g.Select(r => r.ETag)),
                    g.Max(r => r.LastModifiedUtc)))
                .OrderBy(r => r.Uid, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    /// Belirli bir tarih aralığına değen kaynaklar. <c>calendar-query</c>
    /// raporu bunu kullanır; istemci genellikle geçmişin tamamını istemez.
    /// </summary>
    public async Task<List<CalDavResourceInfo>> ListResourcesInRangeAsync(
        Guid calendarId, Instant from, Instant to, CancellationToken ct = default)
    {
        var rows = await db.Events
            .AsNoTracking()
            .Where(e => e.CalendarId == calendarId
                        && e.DeletedAt == null
                        // Tekrarlayan seriler pencereye örnek üretebileceği için
                        // aralık koşulu seri sonuna göre gevşetilir.
                        && ((e.RecurrenceRule == null && e.StartUtc < to && e.EndUtc > from)
                            || (e.RecurrenceRule != null && e.StartUtc < to
                                && (e.SeriesEndUtc == null || e.SeriesEndUtc > from))
                            || e.SeriesId != null))
            .Select(e => new { e.Uid, e.ETag, e.LastModifiedUtc })
            .ToListAsync(ct).ConfigureAwait(false);

        return
        [
            .. rows
                .GroupBy(r => r.Uid, StringComparer.Ordinal)
                .Select(g => new CalDavResourceInfo(
                    g.Key,
                    ComposeETag(g.Select(r => r.ETag)),
                    g.Max(r => r.LastModifiedUtc)))
                .OrderBy(r => r.Uid, StringComparer.Ordinal)
        ];
    }

    /// <summary>Bir kaynağın ICS içeriği: kök ve tüm istisnaları birlikte.</summary>
    public async Task<(string Ics, string ETag)?> GetResourceAsync(
        Guid calendarId, string uid, CancellationToken ct = default)
    {
        var rows = await db.Events
            .AsNoTracking()
            .Include(e => e.Reminders)
            .Where(e => e.CalendarId == calendarId && e.Uid == uid && e.DeletedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);

        if (rows.Count == 0) return null;

        var calendar = await db.Calendars
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == calendarId, ct).ConfigureAwait(false);

        // Kök önce, istisnalar sonra: bazı istemciler bu sırayı bekler.
        var ordered = rows
            .OrderBy(e => e.RecurrenceId.HasValue)
            .ThenBy(e => e.RecurrenceId)
            .ToList();

        return (ics.Export(ordered, calendar?.Name), ComposeETag(rows.Select(e => e.ETag)));
    }

    // ==================================================================
    // Yazma
    // ==================================================================

    /// <summary>
    /// Bir kaynağı oluşturur ya da günceller.
    /// </summary>
    /// <param name="ifMatch">
    /// İstemcinin elindeki etiket. Verilmişse ve uyuşmuyorsa yazma reddedilir:
    /// iki istemci aynı anda düzenlediğinde biri sessizce ötekini ezmemelidir.
    /// </param>
    public async Task<CalDavWriteResult> PutResourceAsync(
        Guid userId,
        Guid calendarId,
        string uid,
        string icsText,
        string? ifMatch,
        CancellationToken ct = default)
    {
        var collection = await GetCollectionAsync(userId, calendarId, ct).ConfigureAwait(false);
        if (collection is null) return new CalDavWriteResult(CalDavWriteOutcome.NotFound);
        if (collection.IsReadOnly) return new CalDavWriteResult(CalDavWriteOutcome.Forbidden);

        var existing = await db.Events
            .Where(e => e.CalendarId == calendarId && e.Uid == uid)
            .ToListAsync(ct).ConfigureAwait(false);

        var live = existing.Where(e => e.DeletedAt is null).ToList();

        if (ifMatch is { Length: > 0 } expected)
        {
            var current = live.Count == 0 ? null : ComposeETag(live.Select(e => e.ETag));
            if (!ETagMatches(expected, current)) return new CalDavWriteResult(CalDavWriteOutcome.Conflict);
        }

        var parsed = ics.Import(icsText, calendarId, collection.Calendar.TimeZoneId);
        if (parsed.Events.Count == 0)
        {
            return new CalDavWriteResult(CalDavWriteOutcome.Invalid,
                Message: parsed.Warnings.Count > 0 ? parsed.Warnings[0] : "Okunabilir VEVENT bulunamadı.");
        }

        // Gönderilen dosyadaki UID, adresteki UID ile aynı olmalı.
        foreach (var incoming in parsed.Events) incoming.Uid = uid;

        var now = clock.GetCurrentInstant();
        var isNew = live.Count == 0;

        // Aynı örneğin iki kaydı olmasın diye eşleştirme RECURRENCE-ID üzerinden yapılır.
        var byRecurrence = live.ToDictionary(e => e.RecurrenceId ?? default, e => e);
        var seen = new HashSet<LocalDateTime>();
        var incomingRoot = parsed.Events.FirstOrDefault(e => e.RecurrenceId is null);

        // İstisnaların bağlanacağı kök kimliği: kök zaten kayıtlıysa onun
        // kimliği, değilse eklenecek olanınki. Gelen dosyadaki kimlikler
        // ayrıştırma sırasında yeni üretildiği için doğrudan kullanılamaz —
        // kullanılırsa yabancı anahtar var olmayan bir satırı gösterir.
        var existingRoot = live.Find(e => e.RecurrenceId is null);
        var rootId = existingRoot?.Id ?? incomingRoot?.Id;

        foreach (var incoming in parsed.Events)
        {
            var key = incoming.RecurrenceId ?? default;
            seen.Add(key);

            if (byRecurrence.TryGetValue(key, out var current))
            {
                CopyInto(incoming, current, now);
                continue;
            }

            incoming.CalendarId = calendarId;
            incoming.LastModifiedUtc = now;
            incoming.UpdatedAt = now.ToDateTimeOffset();
            incoming.DeletedAt = null;

            // İstisnalar kökle ilişkilendirilir; kök henüz kaydedilmemişse bile
            // kimliği bellidir.
            if (incoming.RecurrenceId is not null && rootId is { } id) incoming.SeriesId = id;

            db.Events.Add(incoming);
        }

        // Dosyada artık bulunmayan örnekler kaldırılır.
        foreach (var (key, row) in byRecurrence)
        {
            if (seen.Contains(key)) continue;
            db.Events.Remove(row);
        }

        WriteChangeLog(calendarId, userId, isNew ? ChangeOperation.Create : ChangeOperation.Update,
            rootId ?? Guid.Empty,
            $"CalDAV: \"{incomingRoot?.Title ?? uid}\" {(isNew ? "eklendi" : "güncellendi")}");

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var written = await db.Events
            .AsNoTracking()
            .Where(e => e.CalendarId == calendarId && e.Uid == uid && e.DeletedAt == null)
            .Select(e => e.ETag)
            .ToListAsync(ct).ConfigureAwait(false);

        return new CalDavWriteResult(
            isNew ? CalDavWriteOutcome.Created : CalDavWriteOutcome.Updated,
            ComposeETag(written));
    }

    /// <summary>Bir kaynağı siler. Silinen etkinlikler çöp kutusuna gider.</summary>
    public async Task<CalDavWriteResult> DeleteResourceAsync(
        Guid userId,
        Guid calendarId,
        string uid,
        string? ifMatch,
        CancellationToken ct = default)
    {
        var collection = await GetCollectionAsync(userId, calendarId, ct).ConfigureAwait(false);
        if (collection is null) return new CalDavWriteResult(CalDavWriteOutcome.NotFound);
        if (collection.IsReadOnly) return new CalDavWriteResult(CalDavWriteOutcome.Forbidden);

        var rows = await db.Events
            .Where(e => e.CalendarId == calendarId && e.Uid == uid && e.DeletedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);

        if (rows.Count == 0) return new CalDavWriteResult(CalDavWriteOutcome.NotFound);

        if (ifMatch is { Length: > 0 } expected
            && !ETagMatches(expected, ComposeETag(rows.Select(e => e.ETag))))
        {
            return new CalDavWriteResult(CalDavWriteOutcome.Conflict);
        }

        var deletedAt = clock.GetCurrentInstant().ToDateTimeOffset();
        foreach (var row in rows)
        {
            row.DeletedAt = deletedAt;
            row.UpdatedAt = deletedAt;
        }

        WriteChangeLog(calendarId, userId, ChangeOperation.Delete, rows[0].Id,
            $"CalDAV: \"{rows[0].Title}\" silindi");

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new CalDavWriteResult(CalDavWriteOutcome.Deleted);
    }

    // ==================================================================
    // Artımlı senkronizasyon
    // ==================================================================

    /// <summary>
    /// <paramref name="since"/> imlecinden sonraki değişiklikler.
    /// İstemci ilk kez bağlanıyorsa (imleç 0) her şey "değişmiş" sayılır.
    /// </summary>
    public async Task<CalDavSyncResult> GetChangesAsync(
        Guid calendarId, long since, CancellationToken ct = default)
    {
        var latest = await db.ChangeLog
            .AsNoTracking()
            .Where(c => c.CalendarId == calendarId)
            .OrderByDescending(c => c.SyncToken)
            .Select(c => (long?)c.SyncToken)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? 0;

        if (since <= 0)
        {
            var all = await ListResourcesAsync(calendarId, ct).ConfigureAwait(false);
            return new CalDavSyncResult(all, [], latest);
        }

        // Değişiklik günlüğü etkinlik kimliği tutar; UID'ye çevirmek gerekir.
        var touchedIds = await db.ChangeLog
            .AsNoTracking()
            .Where(c => c.CalendarId == calendarId && c.SyncToken > since && c.EntityType == nameof(Event))
            .Select(c => c.EntityId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        if (touchedIds.Count == 0) return new CalDavSyncResult([], [], latest);

        var touchedUids = await db.Events
            .AsNoTracking()
            .Where(e => touchedIds.Contains(e.Id))
            .Select(e => e.Uid)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        var live = await db.Events
            .AsNoTracking()
            .Where(e => e.CalendarId == calendarId && touchedUids.Contains(e.Uid) && e.DeletedAt == null)
            .Select(e => new { e.Uid, e.ETag, e.LastModifiedUtc })
            .ToListAsync(ct).ConfigureAwait(false);

        var changed = live
            .GroupBy(r => r.Uid, StringComparer.Ordinal)
            .Select(g => new CalDavResourceInfo(
                g.Key, ComposeETag(g.Select(r => r.ETag)), g.Max(r => r.LastModifiedUtc)))
            .ToList();

        // Dokunulmuş ama artık canlı satırı kalmamış UID'ler silinmiştir.
        var removed = touchedUids
            .Except(changed.Select(c => c.Uid), StringComparer.Ordinal)
            .ToList();

        return new CalDavSyncResult(changed, removed, latest);
    }

    // ==================================================================
    // Yardımcılar
    // ==================================================================

    /// <summary>
    /// Bir kaynağın etiketi, ona ait tüm satırların etiketlerinden türetilir.
    /// Yalnızca kökünki kullanılsaydı, bir istisna değişince istemci kaynağın
    /// değiştiğini anlamazdı.
    /// </summary>
    private static string ComposeETag(IEnumerable<string> partETags)
    {
        var joined = string.Join('|', partETags.OrderBy(e => e, StringComparer.Ordinal));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));

        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    /// <summary>If-Match başlığını karşılaştırır. "*" her sürümle eşleşir.</summary>
    private static bool ETagMatches(string ifMatch, string? current)
    {
        if (ifMatch.Trim() == "*") return current is not null;

        foreach (var candidate in ifMatch.Split(',', StringSplitOptions.TrimEntries))
        {
            var cleaned = candidate.Trim('"', ' ');
            if (cleaned.StartsWith("W/", StringComparison.Ordinal)) cleaned = cleaned[2..].Trim('"');

            if (string.Equals(cleaned, current, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private void WriteChangeLog(
        Guid calendarId, Guid actorUserId, ChangeOperation operation, Guid entityId, string summary)
    {
        db.ChangeLog.Add(new ChangeLogEntry
        {
            OperationId = Guid.NewGuid(),
            EntityType = nameof(Event),
            EntityId = entityId,
            CalendarId = calendarId,
            Operation = operation,
            ActorUserId = actorUserId,
            ChangedAt = clock.GetCurrentInstant().ToDateTimeOffset(),
            Summary = summary,
        });
    }

    /// <summary>Gelen kaydın alanlarını var olan satıra taşır; kimlik ve ilişkiler korunur.</summary>
    private static void CopyInto(Event incoming, Event target, Instant now)
    {
        target.Title = incoming.Title;
        target.DescriptionHtml = incoming.DescriptionHtml;
        target.AgendaText = incoming.AgendaText;
        target.LocationText = incoming.LocationText;
        target.StartLocal = incoming.StartLocal;
        target.EndLocal = incoming.EndLocal;
        target.StartTimeZoneId = incoming.StartTimeZoneId;
        target.EndTimeZoneId = incoming.EndTimeZoneId;
        target.IsAllDay = incoming.IsAllDay;
        target.StartUtc = incoming.StartUtc;
        target.EndUtc = incoming.EndUtc;
        target.RecurrenceRule = incoming.RecurrenceRule;
        // Tatil kuralı iCalendar'da karşılığı olmayan yerel bir ayardır;
        // dış istemciden gelen yazma onu silmemeli.
        target.ExDates = incoming.ExDates;
        target.RDates = incoming.RDates;
        target.Availability = incoming.Availability;
        target.Visibility = incoming.Visibility;
        target.Status = incoming.Status;
        target.SearchText = incoming.SearchText;

        target.Sequence = Math.Max(target.Sequence + 1, incoming.Sequence);
        target.ETag = Guid.NewGuid().ToString("N")[..16];
        target.LastModifiedUtc = now;
        target.UpdatedAt = now.ToDateTimeOffset();
        target.DeletedAt = null;
    }
}
