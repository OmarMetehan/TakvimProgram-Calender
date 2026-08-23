using Microsoft.EntityFrameworkCore;
using Takvim.Core.Domain;
using Takvim.Core.Permissions;

namespace Takvim.Data.Services;

/// <summary>
/// Bir kullanıcının o andaki erişim haritası: sahip olduğu takvimler, kendisiyle
/// paylaşılanlar ve davetli olduğu etkinlikler.
/// <para>
/// Her etkinlik için tek tek veritabanına gitmemek üzere bir kez yüklenir ve
/// sorgu boyunca kullanılır.
/// </para>
/// </summary>
public sealed class ViewerScope
{
    public required Guid UserId { get; init; }

    /// <summary>Kullanıcının sahibi olduğu takvimler.</summary>
    public required IReadOnlySet<Guid> OwnedCalendarIds { get; init; }

    /// <summary>Kullanıcıyla paylaşılmış takvimler ve paylaşım koşulları.</summary>
    public required IReadOnlyDictionary<Guid, CalendarShare> Shares { get; init; }

    /// <summary>Kullanıcının davetli olduğu etkinliklerin kimlikleri.</summary>
    public required IReadOnlySet<Guid> InvitedEventIds { get; init; }

    /// <summary>Kullanıcının içeriğini görebileceği tüm takvimler.</summary>
    public IEnumerable<Guid> ReadableCalendarIds => OwnedCalendarIds.Concat(Shares.Keys).Distinct();

    /// <summary>Kullanıcının etkinlik yazabileceği takvimler.</summary>
    public IEnumerable<Guid> WritableCalendarIds
        => OwnedCalendarIds.Concat(Shares.Where(s => s.Value.Level >= SharingLevel.CanEdit).Select(s => s.Key))
            .Distinct();

    /// <summary>Belirli bir etkinlik için izin motoruna verilecek bağlam.</summary>
    public ViewerContext ContextFor(Guid calendarId, Guid eventId, Guid? seriesRootId = null)
    {
        if (OwnedCalendarIds.Contains(calendarId))
        {
            return new ViewerContext(UserId, IsOwner: true);
        }

        var isAttendee = InvitedEventIds.Contains(eventId)
                      || (seriesRootId is { } root && InvitedEventIds.Contains(root));

        if (!Shares.TryGetValue(calendarId, out var share))
        {
            // Paylaşım yok; yalnızca davetlilik bir erişim yolu bırakır.
            return new ViewerContext(UserId, IsAttendee: isAttendee);
        }

        return new ViewerContext(
            UserId,
            IsOwner: false,
            SharingLevel: share.Level,
            IsDelegate: share.IsDelegate,
            DelegateCanSeePrivateItems: share.CanSeePrivateItems,
            IsAttendee: isAttendee);
    }

    /// <summary>Takvimin kendisine erişim; etkinlikten bağımsız yetki soruları için.</summary>
    public SharingLevel LevelFor(Guid calendarId)
    {
        if (OwnedCalendarIds.Contains(calendarId)) return SharingLevel.FullControl;
        return Shares.TryGetValue(calendarId, out var share) ? share.Level : SharingLevel.None;
    }

    public bool CanWriteTo(Guid calendarId) => LevelFor(calendarId) >= SharingLevel.CanEdit;
}

/// <summary>Erişim haritasını veritabanından kurar.</summary>
public sealed class CalendarPermissions(TakvimDbContext db)
{
    /// <summary>
    /// Kullanıcının erişim haritasını yükler.
    /// </summary>
    /// <param name="userId">Bakan kullanıcı.</param>
    /// <param name="ct">İptal belirteci.</param>
    public async Task<ViewerScope> LoadAsync(Guid userId, CancellationToken ct = default)
    {
        var owned = await db.Calendars
            .AsNoTracking()
            .Where(c => c.OwnerUserId == userId && c.DeletedAt == null)
            .Select(c => c.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var shares = await db.CalendarShares
            .AsNoTracking()
            .Include(s => s.Calendar)
            .Where(s => s.GranteeUserId == userId && s.Calendar!.DeletedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);

        var invited = await db.Attendees
            .AsNoTracking()
            .Where(a => a.UserId == userId)
            .Select(a => a.EventId)
            .ToListAsync(ct).ConfigureAwait(false);

        return new ViewerScope
        {
            UserId = userId,
            OwnedCalendarIds = owned.ToHashSet(),
            Shares = shares.ToDictionary(s => s.CalendarId),
            InvitedEventIds = invited.ToHashSet(),
        };
    }

    /// <summary>
    /// Kullanıcının kenar çubuğunda göreceği takvimler: kendi takvimleri ve
    /// kendisiyle paylaşılanlar.
    /// </summary>
    public async Task<List<Calendar>> GetVisibleCalendarsAsync(Guid userId, CancellationToken ct = default)
    {
        var scope = await LoadAsync(userId, ct).ConfigureAwait(false);
        var ids = scope.ReadableCalendarIds.ToList();

        return await db.Calendars
            .AsNoTracking()
            .Include(c => c.Owner)
            .Where(c => ids.Contains(c.Id) && c.DeletedAt == null)
            .OrderBy(c => c.OwnerUserId == userId ? 0 : 1)
            .ThenBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .ToListAsync(ct).ConfigureAwait(false);
    }
}
