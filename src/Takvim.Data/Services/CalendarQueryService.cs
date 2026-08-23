using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Permissions;
using Takvim.Core.Recurrence;
using Takvim.Core.Text;

namespace Takvim.Data.Services;

/// <summary>Görünümleri ve aramayı süzen ölçütler.</summary>
public sealed record OccurrenceFilter
{
    /// <summary>Gösterilecek takvimler. Boşsa kullanıcının erişebildiği tüm takvimler.</summary>
    public IReadOnlyList<Guid> CalendarIds { get; init; } = [];

    /// <summary>Seçili kategoriler. Boşsa kategori süzmesi uygulanmaz.</summary>
    public IReadOnlyList<Guid> CategoryIds { get; init; } = [];

    /// <summary>Serbest metin. Başlık, açıklama ve konumda aranır.</summary>
    public string? SearchTerm { get; init; }

    public IReadOnlyList<Availability> Availabilities { get; init; } = [];

    /// <summary>True ise iptal edilmiş etkinlikler de gösterilir.</summary>
    public bool IncludeCancelled { get; init; }

    /// <summary>
    /// True ise kullanıcının davetli olduğu ama takvimi paylaşılmamış
    /// etkinlikler de getirilir. Görünümlerde açık, dışa aktarmada kapalıdır.
    /// </summary>
    public bool IncludeInvitations { get; init; } = true;
}

/// <summary>
/// Görünümlerin okuma yolu.
/// <para>
/// <b>Tek kapı kuralı:</b> etkinlik okuyan her yol buradan geçer ve her örnek
/// <see cref="CalendarAccess"/> çözümlemesinden geçirilir. Görünmemesi gereken
/// örnekler elenir, kısıtlı olanların içeriği karartılmış bir kopyayla
/// değiştirilir — arayüz yanlışlıkla ham başlığı okusa bile gizli veri sızmaz.
/// </para>
/// </summary>
public sealed class CalendarQueryService(
    TakvimDbContext db,
    RecurrenceExpander expander,
    CalendarPermissions permissions)
{
    /// <summary>
    /// Verilen aralığa değen, <paramref name="viewerUserId"/> kullanıcısının
    /// görmeye yetkili olduğu tüm örnekleri döner.
    /// </summary>
    public async Task<List<EventOccurrence>> GetOccurrencesAsync(
        Guid viewerUserId,
        Instant fromInclusive,
        Instant toExclusive,
        OccurrenceFilter? filter = null,
        CancellationToken ct = default)
    {
        filter ??= new OccurrenceFilter();

        var scope = await permissions.LoadAsync(viewerUserId, ct).ConfigureAwait(false);
        var roots = await QueryRootsAsync(scope, fromInclusive, toExclusive, filter, ct).ConfigureAwait(false);
        if (roots.Count == 0) return [];

        // Serilerin istisnaları ayrı çekilir: pencerenin dışından içine taşınmış
        // örnekler kök sorgusunun aralık koşuluna takılmaz.
        var seriesIds = roots.Where(e => e.RecurrenceRule != null).Select(e => e.Id).ToList();
        var exceptions = seriesIds.Count == 0
            ? []
            : await db.Events
                .AsNoTracking()
                .Include(e => e.Categories)
                .Where(e => e.SeriesId != null
                            && seriesIds.Contains(e.SeriesId.Value)
                            && e.DeletedAt == null)
                .ToListAsync(ct).ConfigureAwait(false);

        var occurrences = expander.ExpandMany(
            roots,
            exceptions.ToLookup(e => e.SeriesId!.Value),
            fromInclusive,
            toExclusive);

        var privateCategoryIds = await GetPrivateCategoryIdsAsync(ct).ConfigureAwait(false);

        return ApplyAccessAndFilters(occurrences, scope, privateCategoryIds, filter);
    }

    /// <summary>Serbest metin araması.</summary>
    public Task<List<EventOccurrence>> SearchAsync(
        Guid viewerUserId,
        string term,
        Instant fromInclusive,
        Instant toExclusive,
        OccurrenceFilter? filter = null,
        CancellationToken ct = default)
        => GetOccurrencesAsync(viewerUserId, fromInclusive, toExclusive,
            (filter ?? new OccurrenceFilter()) with { SearchTerm = term }, ct);

    /// <summary>Kullanıcının çöp kutusu. Yalnızca kendi takvimlerini kapsar.</summary>
    public async Task<List<Event>> GetTrashAsync(Guid viewerUserId, CancellationToken ct = default)
    {
        var scope = await permissions.LoadAsync(viewerUserId, ct).ConfigureAwait(false);
        var owned = scope.OwnedCalendarIds.ToList();

        return await db.Events
            .AsNoTracking()
            .Include(e => e.Calendar)
            .Where(e => e.DeletedAt != null && owned.Contains(e.CalendarId))
            .OrderByDescending(e => e.DeletedAt)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Belirli bir örneği bulur. Düzenleme ekranı bunu kullanır.</summary>
    public async Task<EventOccurrence?> FindOccurrenceAsync(
        Guid viewerUserId,
        Guid eventId,
        LocalDateTime? recurrenceId,
        CancellationToken ct = default)
    {
        var ev = await db.Events
            .AsNoTracking()
            .Include(e => e.Calendar)
            .Include(e => e.Reminders)
            .Include(e => e.Categories)
            .Include(e => e.Attendees)
            .FirstOrDefaultAsync(e => e.Id == eventId, ct).ConfigureAwait(false);

        if (ev is null) return null;

        var scope = await permissions.LoadAsync(viewerUserId, ct).ConfigureAwait(false);
        var privateCategoryIds = await GetPrivateCategoryIdsAsync(ct).ConfigureAwait(false);

        EventOccurrence? occurrence;

        if (recurrenceId is null || ev.RecurrenceRule is null)
        {
            occurrence = new EventOccurrence
            {
                Source = ev,
                StartLocal = ev.StartLocal,
                EndLocal = ev.EndLocal,
                StartUtc = ev.StartUtc,
                EndUtc = ev.EndUtc,
                RecurrenceId = ev.RecurrenceId,
                IsException = ev.SeriesId is not null,
            };
        }
        else
        {
            var exceptions = await db.Events
                .AsNoTracking()
                .Where(e => e.SeriesId == ev.Id && e.DeletedAt == null)
                .ToListAsync(ct).ConfigureAwait(false);

            var target = recurrenceId.Value;
            var probeFrom = ev.StartUtc;
            var candidate = Instant.FromDateTimeUtc(DateTime.SpecifyKind(
                target.PlusDays(-1).ToDateTimeUnspecified(), DateTimeKind.Utc));
            if (candidate > probeFrom) probeFrom = candidate;

            occurrence = expander
                .Expand(ev, exceptions, probeFrom, probeFrom + Duration.FromDays(3))
                .FirstOrDefault(o => o.RecurrenceId == target);
        }

        if (occurrence is null) return null;

        var resolved = Resolve(occurrence, scope, privateCategoryIds);
        return resolved.Access.IsHidden ? null : resolved;
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Pencereye örnek üretebilecek kök satırları çeker.
    /// Erişim haritası burada kaba süzgeç olarak kullanılır: kullanıcının hiç
    /// göremeyeceği takvimler veritabanından hiç okunmaz.
    /// </summary>
    private async Task<List<Event>> QueryRootsAsync(
        ViewerScope scope, Instant from, Instant to, OccurrenceFilter filter, CancellationToken ct)
    {
        var readable = scope.ReadableCalendarIds.ToList();

        // Kullanıcı bir takvimi göremese de davetli olduğu etkinliği görür.
        var invited = filter.IncludeInvitations ? scope.InvitedEventIds.ToList() : [];

        // Kenar çubuğunda gizlenen takvimler ayrıca elenir.
        if (filter.CalendarIds.Count > 0)
        {
            var requested = filter.CalendarIds.ToHashSet();
            readable = [.. readable.Where(requested.Contains)];
        }

        if (readable.Count == 0 && invited.Count == 0) return [];

        var query = db.Events
            .AsNoTracking()
            .Include(e => e.Calendar)
            .Include(e => e.Categories)
            .Where(e => e.DeletedAt == null && e.SeriesId == null)
            .Where(e => readable.Contains(e.CalendarId) || invited.Contains(e.Id));

        if (!filter.IncludeCancelled)
            query = query.Where(e => e.Status != EventStatus.Cancelled);

        if (!string.IsNullOrWhiteSpace(filter.SearchTerm))
        {
            var normalized = TurkishText.Normalize(filter.SearchTerm);
            query = query.Where(e => e.SearchText.Contains(normalized));
        }

        query = query.Where(e =>
            // Tekrarlamayanlar: aralıkla kesişenler.
            (e.RecurrenceRule == null && e.StartUtc < to && e.EndUtc > from)
            // Seriler: başlangıcı pencereden önce ve sonu pencereden sonra (ya da süresiz).
            || (e.RecurrenceRule != null && e.StartUtc < to
                && (e.SeriesEndUtc == null || e.SeriesEndUtc > from)));

        return await query.ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Gizli işaretli kategorilerin kimlikleri; izin çözümlemesinin dördüncü boyutu.</summary>
    private Task<List<Guid>> GetPrivateCategoryIdsAsync(CancellationToken ct)
        => db.Categories.AsNoTracking().Where(c => c.IsPrivate).Select(c => c.Id).ToListAsync(ct);

    /// <summary>
    /// Her örneği izin motorundan geçirir, görünmeyecekleri eler, kısıtlı
    /// olanları karartır ve sonra kullanıcı süzgeçlerini uygular.
    /// </summary>
    private static List<EventOccurrence> ApplyAccessAndFilters(
        List<EventOccurrence> occurrences,
        ViewerScope scope,
        List<Guid> privateCategoryIds,
        OccurrenceFilter filter)
    {
        var visible = new List<EventOccurrence>(occurrences.Count);

        foreach (var occurrence in occurrences)
        {
            var resolved = Resolve(occurrence, scope, privateCategoryIds);
            if (resolved.Access.IsHidden) continue;

            visible.Add(resolved);
        }

        IEnumerable<EventOccurrence> result = visible;

        if (filter.CategoryIds.Count > 0)
        {
            var wanted = filter.CategoryIds.ToHashSet();
            // Karartılmış örneklerde kategori bilgisi de gizlidir; süzgece takılmazlar.
            result = result.Where(o => o.CanSeeDetails
                                    && o.Source.Categories.Any(c => wanted.Contains(c.CategoryId)));
        }

        if (filter.Availabilities.Count > 0)
        {
            var wanted = filter.Availabilities.ToHashSet();
            result = result.Where(o => wanted.Contains(o.Source.Availability));
        }

        if (!string.IsNullOrWhiteSpace(filter.SearchTerm))
        {
            // Kök sorgusu metne göre elemiş olabilir; istisnalar ve karartılmış
            // örnekler ayrıca sınanır. Karartılmış bir örnek aramada çıkmamalıdır.
            var term = filter.SearchTerm;
            result = result.Where(o => o.CanSeeDetails && TurkishText.Contains(o.Source.SearchText, term));
        }

        return [.. result];
    }

    /// <summary>Tek bir örnek için erişimi çözer ve gerekiyorsa içeriği karartır.</summary>
    private static EventOccurrence Resolve(
        EventOccurrence occurrence, ViewerScope scope, List<Guid> privateCategoryIds)
    {
        var source = occurrence.Source;

        var context = scope.ContextFor(source.CalendarId, occurrence.EventId, occurrence.SeriesRootId);
        var facts = new EventFacts(
            source.Visibility,
            source.Categories.Any(c => privateCategoryIds.Contains(c.CategoryId)),
            source.Availability,
            source.DeletedAt is not null);

        var access = CalendarAccess.Resolve(context, facts);

        if (access.IsHidden || access.CanSeeDetails)
        {
            return occurrence with { Access = access };
        }

        // Kısıtlı örnek: kaynağı karartılmış bir kopyayla değiştiririz. Böylece
        // arayüz Source.Title okusa bile gizli veri görünmez.
        return occurrence with { Access = access, Source = Redact(source, access) };
    }

    /// <summary>Görülmemesi gereken alanları temizlenmiş bir etkinlik kopyası üretir.</summary>
    private static Event Redact(Event source, EventAccess access) => new()
    {
        Id = source.Id,
        CalendarId = source.CalendarId,
        Uid = source.Uid,
        ETag = source.ETag,
        SeriesId = source.SeriesId,
        RecurrenceId = source.RecurrenceId,
        RecurrenceRule = source.RecurrenceRule,
        StartLocal = source.StartLocal,
        EndLocal = source.EndLocal,
        StartTimeZoneId = source.StartTimeZoneId,
        EndTimeZoneId = source.EndTimeZoneId,
        StartUtc = source.StartUtc,
        EndUtc = source.EndUtc,
        IsAllDay = source.IsAllDay,
        Availability = source.Availability,
        Visibility = source.Visibility,
        Status = source.Status,

        // Başlık yalnızca "başlık ve konum" seviyesinde kalır.
        Title = CalendarAccess.TitleFor(access, source.Title),
        LocationText = CalendarAccess.LocationFor(access, source.LocationText),

        // Açıklama, bağlantı, renk, kategori ve katılımcılar hiç taşınmaz.
        DescriptionHtml = null,
        OnlineMeetingUrl = null,
        OnlineMeetingProvider = null,
        Color = null,
        SearchText = string.Empty,
    };
}
