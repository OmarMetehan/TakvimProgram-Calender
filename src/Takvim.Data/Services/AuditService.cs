using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;

namespace Takvim.Data.Services;

/// <summary>Denetim görünümünün süzgeci.</summary>
public sealed record AuditFilter
{
    /// <summary>Yalnızca bu kişinin yaptığı değişiklikler.</summary>
    public Guid? ActorUserId { get; init; }

    /// <summary>Yalnızca bu takvimi ilgilendiren değişiklikler.</summary>
    public Guid? CalendarId { get; init; }

    /// <summary>Varlık türü, ör. "Event", "CalendarShare".</summary>
    public string? EntityType { get; init; }

    public ChangeOperation? Operation { get; init; }

    /// <summary>Bu andan sonrası.</summary>
    public Instant? Since { get; init; }

    /// <summary>Özet metninde aranan ifade.</summary>
    public string? Term { get; init; }
}

/// <summary>Denetim görünümünde tek bir satır.</summary>
/// <param name="Entry">Kaydın kendisi.</param>
/// <param name="ActorName">İşlemi yapanın adı; hesap silinmişse null.</param>
/// <param name="CalendarName">İlgili takvimin adı; yoksa null.</param>
public sealed record AuditRow(ChangeLogEntry Entry, string? ActorName, string? CalendarName);

/// <summary>
/// Denetim görünümü: kim, neyi, ne zaman değiştirdi.
/// <para>
/// Kayıtları <see cref="ChangeLogWriter"/> zaten yazıyordu; okunacak bir yer
/// yoktu. Tablo yalnızca eklenir — hiçbir satır güncellenmez ya da silinmez —
/// bu yüzden burada da yalnızca okuma vardır.
/// </para>
/// <para>
/// Görünüm <b>kullanıcının erişebildiği</b> takvimlerle sınırlıdır. Günlük
/// veriden daha az korunaklı olsaydı, gizli bir etkinliğin başlığı özet
/// satırında sızardı.
/// </para>
/// </summary>
public sealed class AuditService(TakvimDbContext db, CalendarPermissions permissions)
{
    /// <summary>Tek seferde okunacak en fazla satır.</summary>
    public const int PageSize = 100;

    /// <summary>
    /// Denetim kayıtlarını okur; yeniden eskiye.
    /// </summary>
    /// <param name="beforeToken">
    /// Sayfalama imleci: verilirse bu değerden küçük satırlar döner. İlk sayfa
    /// için null geçilir.
    /// </param>
    public async Task<List<AuditRow>> GetAsync(
        Guid viewerUserId,
        AuditFilter? filter = null,
        long? beforeToken = null,
        CancellationToken ct = default)
    {
        filter ??= new AuditFilter();

        var scope = await permissions.LoadAsync(viewerUserId, ct).ConfigureAwait(false);
        var readable = scope.ReadableCalendarIds.ToList();

        var query = db.ChangeLog.AsNoTracking();

        // Takvimi olmayan kayıtlar (hesap, paylaşım) yalnızca sahibine görünür:
        // başkasının hesap işlemlerini görmenin bir gerekçesi yok.
        query = query.Where(c => (c.CalendarId != null && readable.Contains(c.CalendarId.Value))
                              || c.ActorUserId == viewerUserId);

        if (filter.ActorUserId is { } actor) query = query.Where(c => c.ActorUserId == actor);
        if (filter.CalendarId is { } calendar) query = query.Where(c => c.CalendarId == calendar);
        if (filter.Operation is { } operation) query = query.Where(c => c.Operation == operation);

        if (!string.IsNullOrWhiteSpace(filter.EntityType))
        {
            var type = filter.EntityType;
            query = query.Where(c => c.EntityType == type);
        }

        if (filter.Since is { } since)
        {
            var moment = since.ToDateTimeOffset();
            query = query.Where(c => c.ChangedAt >= moment);
        }

        if (!string.IsNullOrWhiteSpace(filter.Term))
        {
            var term = filter.Term;
            query = query.Where(c => c.Summary != null && c.Summary.Contains(term));
        }

        if (beforeToken is { } token) query = query.Where(c => c.SyncToken < token);

        var entries = await query
            .OrderByDescending(c => c.SyncToken)
            .Take(PageSize)
            .ToListAsync(ct).ConfigureAwait(false);

        return await DecorateAsync(entries, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Tek bir işlemin tüm satırları. "Bu ve sonrakiler" gibi düzenlemeler
    /// birden çok satıra dokunur; kullanıcı hepsini birlikte görmelidir.
    /// </summary>
    public async Task<List<AuditRow>> GetOperationAsync(
        Guid operationId, CancellationToken ct = default)
    {
        var entries = await db.ChangeLog
            .AsNoTracking()
            .Where(c => c.OperationId == operationId)
            .OrderBy(c => c.SyncToken)
            .ToListAsync(ct).ConfigureAwait(false);

        return await DecorateAsync(entries, ct).ConfigureAwait(false);
    }

    /// <summary>Süzgeç açılır kutularını doldurmak için kullanılan türler.</summary>
    public Task<List<string>> GetEntityTypesAsync(CancellationToken ct = default)
        => db.ChangeLog
            .AsNoTracking()
            .Select(c => c.EntityType)
            .Distinct()
            .OrderBy(t => t)
            .ToListAsync(ct);

    // ==================================================================

    /// <summary>
    /// Kimlikleri adlarla değiştirir. Tek tek sorgu yerine toplu okuma yapılır:
    /// yüz satırlık bir sayfa, yüz sorgu açmamalı.
    /// </summary>
    private async Task<List<AuditRow>> DecorateAsync(
        List<ChangeLogEntry> entries, CancellationToken ct)
    {
        if (entries.Count == 0) return [];

        var userIds = entries
            .Select(e => e.ActorUserId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        var calendarIds = entries
            .Select(e => e.CalendarId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        var users = await db.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct).ConfigureAwait(false);

        var calendars = await db.Calendars
            .AsNoTracking()
            .Where(c => calendarIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct).ConfigureAwait(false);

        return [.. entries.Select(entry => new AuditRow(
            entry,
            entry.ActorUserId is { } actor ? users.GetValueOrDefault(actor) : null,
            entry.CalendarId is { } calendar ? calendars.GetValueOrDefault(calendar) : null))];
    }

    /// <summary>Varlık türünün Türkçe adı.</summary>
    public static string DescribeEntity(string entityType) => entityType switch
    {
        nameof(Event) => "Etkinlik",
        nameof(Calendar) => "Takvim",
        nameof(Category) => "Kategori",
        nameof(CalendarShare) => "Paylaşım",
        nameof(Attendee) => "Katılımcı",
        nameof(TaskItem) => "Görev",
        nameof(Resource) => "Kaynak",
        _ => entityType,
    };

    /// <summary>İşlem türünün Türkçe adı.</summary>
    public static string DescribeOperation(ChangeOperation operation) => operation switch
    {
        ChangeOperation.Create => "eklendi",
        ChangeOperation.Update => "değiştirildi",
        ChangeOperation.Delete => "silindi",
        ChangeOperation.Restore => "geri alındı",
        _ => operation.ToString(),
    };
}
