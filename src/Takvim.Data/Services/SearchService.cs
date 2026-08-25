using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Recurrence;
using Takvim.Core.Time;

namespace Takvim.Data.Services;

/// <summary>
/// Gelişmiş arama ve kaydedilmiş aramalar.
/// <para>
/// Aramanın kendisi <see cref="CalendarQueryService"/> üzerinden yürür; burada
/// yapılan tek şey ölçütleri bir tarih aralığına ve bir süzgece çevirmektir.
/// Böylece arama da "tek kapı" kuralının içinde kalır: izin çözümlemesini
/// atlayan ikinci bir okuma yolu açılmaz.
/// </para>
/// </summary>
public sealed class SearchService(
    TakvimDbContext db,
    CalendarQueryService query,
    TimeZoneService zones,
    IClock clock)
{
    /// <summary>Bir kullanıcının tutabileceği en fazla kayıtlı arama.</summary>
    public const int MaxPerUser = 30;

    /// <summary>Sonuç listesinde gösterilecek en fazla örnek.</summary>
    public const int MaxResults = 500;

    /// <summary>"Tümü" seçildiğinde taranan pencerenin yarı genişliği.</summary>
    private const int AllRangeYears = 5;

    // ==================================================================
    // Arama
    // ==================================================================

    /// <summary>Ölçütleri çalıştırır. Sonuçlar zamana göre sıralı gelir.</summary>
    public async Task<List<EventOccurrence>> RunAsync(
        Guid viewerUserId,
        SearchCriteria criteria,
        string? zoneId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        if (criteria.IsEmpty) return [];

        var (from, to) = ResolveRange(criteria.Range, zoneId);

        var results = await query.GetOccurrencesAsync(
            viewerUserId, from, to, ToFilter(criteria), ct).ConfigureAwait(false);

        // Yakın tarihler önce: "gelecek" aramasında en yakın toplantı üstte,
        // "geçmiş" aramasında en son olan üstte.
        results = criteria.Range == SearchRange.Past
            ? [.. results.OrderByDescending(o => o.StartUtc)]
            : [.. results.OrderBy(o => o.StartUtc)];

        return results.Count > MaxResults ? results.GetRange(0, MaxResults) : results;
    }

    /// <summary>Ölçütlerin sorgu süzgeci karşılığı.</summary>
    public static OccurrenceFilter ToFilter(SearchCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        return new OccurrenceFilter
        {
            SearchTerm = string.IsNullOrWhiteSpace(criteria.Term) ? null : criteria.Term,
            CalendarIds = criteria.CalendarIds,
            CategoryIds = criteria.CategoryIds,
            Availabilities = criteria.Availabilities,
            IncludeCancelled = criteria.IncludeCancelled,
            OrganizerUserId = criteria.OrganizerUserId,
            AttendeeUserId = criteria.AttendeeUserId,
            HasAttendees = criteria.HasAttendees,
            IsRecurring = criteria.IsRecurring,
            IsAllDay = criteria.IsAllDay,
            HasAttachments = criteria.HasAttachments,
            HasOnlineMeeting = criteria.HasOnlineMeeting,
        };
    }

    /// <summary>Kayan pencereyi mutlak ana çevirir.</summary>
    public (Instant From, Instant To) ResolveRange(SearchRange range, string? zoneId = null)
    {
        var zone = zoneId ?? TimeZoneService.DefaultZoneId;
        var today = zones.ToLocal(clock.GetCurrentInstant(), zone).Date;

        return range switch
        {
            SearchRange.Upcoming => (
                zones.ToInstant(today.AtMidnight(), zone),
                zones.ToInstant(today.PlusYears(AllRangeYears).AtMidnight(), zone)),

            SearchRange.Past => (
                zones.ToInstant(today.PlusYears(-AllRangeYears).AtMidnight(), zone),
                zones.ToInstant(today.PlusDays(1).AtMidnight(), zone)),

            SearchRange.ThisYear => (
                zones.ToInstant(new LocalDate(today.Year, 1, 1).AtMidnight(), zone),
                zones.ToInstant(new LocalDate(today.Year + 1, 1, 1).AtMidnight(), zone)),

            _ => (
                zones.ToInstant(today.PlusYears(-AllRangeYears).AtMidnight(), zone),
                zones.ToInstant(today.PlusYears(AllRangeYears).AtMidnight(), zone)),
        };
    }

    // ==================================================================
    // Kaydedilmiş aramalar
    // ==================================================================

    public Task<List<SavedSearch>> GetSavedAsync(Guid userId, CancellationToken ct = default)
        => db.SavedSearches
            .AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.UseCount)
            .ThenBy(s => s.Name)
            .ToListAsync(ct);

    /// <summary>Aynı adlı arama varsa üzerine yazılır.</summary>
    public async Task<SavedSearch?> SaveAsync(
        Guid userId, string name, SearchCriteria criteria, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(criteria);

        var trimmed = name.Trim();

        var existing = await db.SavedSearches
            .FirstOrDefaultAsync(s => s.UserId == userId && s.Name == trimmed, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            existing.CriteriaJson = SavedSearch.Write(criteria);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            return existing;
        }

        var count = await db.SavedSearches.CountAsync(s => s.UserId == userId, ct).ConfigureAwait(false);
        if (count >= MaxPerUser) return null;

        var saved = new SavedSearch
        {
            UserId = userId,
            Name = trimmed,
            CriteriaJson = SavedSearch.Write(criteria),
            CreatedAt = clock.GetCurrentInstant().ToDateTimeOffset(),
        };

        db.SavedSearches.Add(saved);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return saved;
    }

    /// <summary>Kayıtlı aramanın ölçütlerini döner ve kullanıldı olarak işaretler.</summary>
    public async Task<SearchCriteria?> UseAsync(Guid savedSearchId, CancellationToken ct = default)
    {
        var saved = await db.SavedSearches
            .FirstOrDefaultAsync(s => s.Id == savedSearchId, ct).ConfigureAwait(false);

        if (saved is null) return null;

        saved.UseCount++;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return saved.Read();
    }

    public async Task DeleteSavedAsync(Guid savedSearchId, CancellationToken ct = default)
        => await db.SavedSearches
            .Where(s => s.Id == savedSearchId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
}
