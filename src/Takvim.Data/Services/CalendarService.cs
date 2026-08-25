using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Time;

namespace Takvim.Data.Services;

/// <summary>Takvim oluşturma ve düzenleme girdisi.</summary>
public sealed record CalendarInput
{
    public required string Name { get; init; }

    public string Color { get; init; } = "peacock";
    public string? Description { get; init; }

    public string TimeZoneId { get; init; } = TimeZoneService.DefaultZoneId;

    /// <summary>Zamanlı etkinliklerin varsayılan hatırlatıcısı. Null ise hatırlatıcı yok.</summary>
    public int? DefaultReminderMinutes { get; init; } = 10;

    /// <summary>Tüm gün etkinlikleri için ayrı varsayılan.</summary>
    public int? DefaultAllDayReminderMinutes { get; init; } = 12 * 60;
}

/// <summary>Bir takvimin silinmeden önce gösterilecek özeti.</summary>
/// <param name="EventCount">İçindeki silinmemiş etkinlik sayısı.</param>
/// <param name="ShareCount">Kaç kişiyle paylaşıldığı.</param>
public sealed record CalendarUsage(int EventCount, int ShareCount);

/// <summary>
/// Takvim yönetimi: açma, adlandırma, renklendirme, sıralama, silme.
/// <para>
/// Her takvim aynı yoldan yönetilmez. Kaynak takvimleri
/// <see cref="ResourceService"/>, abone takvimleri
/// <see cref="SubscriptionService"/> yönetir; ikisi de kendi kayıtlarıyla
/// birlikte hareket etmek zorundadır. Bu servis <b>kullanıcının kendi
/// açtığı</b> takvimlerle ilgilenir ve ötekilere dokunmayı reddeder.
/// </para>
/// </summary>
public sealed class CalendarService(TakvimDbContext db, TimeZoneService zones, IClock clock)
{
    /// <summary>Silinen takvimin çöp kutusunda kalma süresi; etkinliklerle aynı.</summary>
    public static readonly Duration TrashRetention = Duration.FromDays(30);

    /// <summary>Bir kullanıcının açabileceği en fazla takvim.</summary>
    public const int MaxPerUser = 40;

    // ==================================================================
    // Okuma
    // ==================================================================

    /// <summary>Kullanıcının sahibi olduğu, silinmemiş takvimler.</summary>
    public Task<List<Calendar>> GetOwnAsync(Guid userId, CancellationToken ct = default)
        => db.Calendars
            .AsNoTracking()
            .Where(c => c.OwnerUserId == userId && c.DeletedAt == null)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .ToListAsync(ct);

    /// <summary>Çöp kutusundaki takvimler.</summary>
    public Task<List<Calendar>> GetDeletedAsync(Guid userId, CancellationToken ct = default)
        => db.Calendars
            .AsNoTracking()
            .Where(c => c.OwnerUserId == userId && c.DeletedAt != null)
            .OrderByDescending(c => c.DeletedAt)
            .ToListAsync(ct);

    /// <summary>Silmeden önce kullanıcıya ne kaybedeceğini söylemek için.</summary>
    public async Task<CalendarUsage> GetUsageAsync(Guid calendarId, CancellationToken ct = default)
    {
        var events = await db.Events
            .CountAsync(e => e.CalendarId == calendarId && e.DeletedAt == null, ct).ConfigureAwait(false);

        var shares = await db.CalendarShares
            .CountAsync(s => s.CalendarId == calendarId, ct).ConfigureAwait(false);

        return new CalendarUsage(events, shares);
    }

    // ==================================================================
    // Yazma
    // ==================================================================

    public async Task<Calendar> CreateAsync(
        Guid userId, CalendarInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Name);

        var count = await db.Calendars
            .CountAsync(c => c.OwnerUserId == userId && c.DeletedAt == null, ct).ConfigureAwait(false);

        if (count >= MaxPerUser)
        {
            throw new InvalidOperationException($"En fazla {MaxPerUser} takvim açılabilir.");
        }

        var now = clock.GetCurrentInstant().ToDateTimeOffset();

        var calendar = new Calendar
        {
            OwnerUserId = userId,
            Name = input.Name.Trim(),
            SortOrder = count,
            CreatedAt = now,
            UpdatedAt = now,
        };

        Apply(calendar, input);

        db.Calendars.Add(calendar);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return calendar;
    }

    /// <summary>
    /// Takvimi günceller.
    /// <para>
    /// Kaynak ve abone takvimleri buradan düzenlenmez: adları kendi
    /// kayıtlarını izler ve burada değiştirilseler bir sonraki eşitlemede
    /// geri dönerlerdi.
    /// </para>
    /// </summary>
    /// <returns>Güncellendiyse null, güncellenemediyse gerekçesi.</returns>
    public async Task<string?> UpdateAsync(
        Guid calendarId, CalendarInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Name);

        var calendar = await db.Calendars
            .FirstOrDefaultAsync(c => c.Id == calendarId, ct).ConfigureAwait(false);

        if (calendar is null) return "Takvim bulunamadı.";
        if (ManagedElsewhere(calendar.Kind) is { } reason) return reason;

        calendar.Name = input.Name.Trim();
        Apply(calendar, input);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return null;
    }

    /// <summary>Yalnızca rengi değiştirir; kenar çubuğundan hızlı erişim için.</summary>
    public async Task SetColorAsync(Guid calendarId, string color, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(color);

        var calendar = await db.Calendars
            .FirstOrDefaultAsync(c => c.Id == calendarId, ct).ConfigureAwait(false);

        if (calendar is null) return;

        calendar.Color = color;
        calendar.UpdatedAt = clock.GetCurrentInstant().ToDateTimeOffset();

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Takvimleri verilen sıraya dizer.</summary>
    public async Task ReorderAsync(
        Guid userId, IReadOnlyList<Guid> orderedIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);

        var calendars = await db.Calendars
            .Where(c => c.OwnerUserId == userId && c.DeletedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);

        for (var i = 0; i < orderedIds.Count; i++)
        {
            if (calendars.Find(c => c.Id == orderedIds[i]) is { } calendar) calendar.SortOrder = i;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ==================================================================
    // Silme
    // ==================================================================

    /// <summary>
    /// Takvimi çöp kutusuna atar. Etkinlikleri de birlikte gider ama silinmez:
    /// takvim geri alınırsa etkinlikleri de geri gelmelidir.
    /// <para>
    /// Kişisel takvim silinemez: hesabın kendisine aittir ve silinirse
    /// kullanıcı etkinlik ekleyecek yer bulamaz.
    /// </para>
    /// </summary>
    /// <returns>Silindiyse null, silinemediyse gerekçesi.</returns>
    public async Task<string?> DeleteAsync(Guid calendarId, CancellationToken ct = default)
    {
        var calendar = await db.Calendars
            .FirstOrDefaultAsync(c => c.Id == calendarId, ct).ConfigureAwait(false);

        if (calendar is null) return "Takvim bulunamadı.";
        if (ManagedElsewhere(calendar.Kind) is { } reason) return reason;

        if (calendar.Kind == CalendarKind.Personal)
        {
            var remaining = await db.Calendars
                .CountAsync(c => c.OwnerUserId == calendar.OwnerUserId
                              && c.DeletedAt == null
                              && c.Kind == CalendarKind.Personal, ct).ConfigureAwait(false);

            if (remaining <= 1) return "Son kişisel takvim silinemez.";
        }

        var now = clock.GetCurrentInstant().ToDateTimeOffset();

        calendar.DeletedAt = now;

        // Etkinlikler de çöp kutusuna gider; takvim geri alınırsa geri gelirler.
        await db.Events
            .Where(e => e.CalendarId == calendarId && e.DeletedAt == null)
            .ExecuteUpdateAsync(e => e.SetProperty(x => x.DeletedAt, now), ct).ConfigureAwait(false);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return null;
    }

    /// <summary>
    /// Takvimi geri alır. Etkinliklerinden yalnızca <b>takvimle birlikte</b>
    /// silinenler döner: takvim silinmeden önce tek tek silinmiş etkinlikler
    /// silinmiş kalmalıdır.
    /// </summary>
    public async Task RestoreAsync(Guid calendarId, CancellationToken ct = default)
    {
        var calendar = await db.Calendars
            .FirstOrDefaultAsync(c => c.Id == calendarId, ct).ConfigureAwait(false);

        if (calendar?.DeletedAt is not { } deletedAt) return;

        calendar.DeletedAt = null;

        await db.Events
            .Where(e => e.CalendarId == calendarId && e.DeletedAt == deletedAt)
            .ExecuteUpdateAsync(e => e.SetProperty(x => x.DeletedAt, (DateTimeOffset?)null), ct)
            .ConfigureAwait(false);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Saklama süresi dolan takvimleri kalıcı siler.</summary>
    public async Task<int> PurgeTrashAsync(CancellationToken ct = default)
    {
        var cutoff = clock.GetCurrentInstant().Minus(TrashRetention).ToDateTimeOffset();

        return await db.Calendars
            .Where(c => c.DeletedAt != null && c.DeletedAt < cutoff)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    // ==================================================================

    private void Apply(Calendar calendar, CalendarInput input)
    {
        calendar.Color = input.Color;
        calendar.Description = input.Description;
        calendar.DefaultReminderMinutes = input.DefaultReminderMinutes;
        calendar.DefaultAllDayReminderMinutes = input.DefaultAllDayReminderMinutes;

        // Bilinmeyen bir dilim, etkinliklerin saatini bozar; varsayılana düşülür.
        calendar.TimeZoneId = zones.IsKnown(input.TimeZoneId)
            ? input.TimeZoneId
            : TimeZoneService.DefaultZoneId;

        calendar.UpdatedAt = clock.GetCurrentInstant().ToDateTimeOffset();
    }

    /// <summary>
    /// Bu tür başka bir servisin sorumluluğundaysa gerekçesini döner.
    /// Buradan yönetilmesi, iki kaydın ayrışmasına yol açardı.
    /// </summary>
    private static string? ManagedElsewhere(CalendarKind kind) => kind switch
    {
        CalendarKind.Resource => "Kaynak takvimi \"Odalar ve ekipman\" bölümünden yönetilir.",
        CalendarKind.Subscribed => "Abone takvim \"Takvim abonelikleri\" bölümünden yönetilir.",
        CalendarKind.Holiday => "Resmi tatil takvimi yerleşiktir; değiştirilemez.",
        _ => null,
    };
}
