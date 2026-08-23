using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Recurrence;
using Takvim.Core.Text;

namespace Takvim.Data.Services;

/// <summary>Görünümleri ve aramayı süzen ölçütler.</summary>
public sealed record OccurrenceFilter
{
    /// <summary>Gösterilecek takvimler. Boşsa tüm görünür takvimler.</summary>
    public IReadOnlyList<Guid> CalendarIds { get; init; } = [];

    /// <summary>Seçili kategoriler. Boşsa kategori süzmesi uygulanmaz.</summary>
    public IReadOnlyList<Guid> CategoryIds { get; init; } = [];

    /// <summary>Serbest metin. Başlık, açıklama ve konumda aranır.</summary>
    public string? SearchTerm { get; init; }

    public IReadOnlyList<Availability> Availabilities { get; init; } = [];

    /// <summary>True ise iptal edilmiş etkinlikler de gösterilir.</summary>
    public bool IncludeCancelled { get; init; }
}

/// <summary>
/// Görünümlerin okuma yolu. Bir tarih aralığı için gereken etkinlikleri çeker,
/// tekrarlama motorundan geçirir ve çizilmeye hazır örnekleri döner.
/// </summary>
public sealed class CalendarQueryService(TakvimDbContext db, RecurrenceExpander expander)
{
    /// <summary>
    /// Verilen aralığa değen tüm örnekleri döner.
    /// </summary>
    /// <param name="fromInclusive">Aralığın başlangıcı, dahil.</param>
    /// <param name="toExclusive">Aralığın bitişi, hariç.</param>
    public async Task<List<EventOccurrence>> GetOccurrencesAsync(
        Instant fromInclusive,
        Instant toExclusive,
        OccurrenceFilter? filter = null,
        CancellationToken ct = default)
    {
        filter ??= new OccurrenceFilter();

        var roots = await QueryRootsAsync(fromInclusive, toExclusive, filter, ct).ConfigureAwait(false);
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

        return ApplyPostFilters(occurrences, filter);
    }

    /// <summary>
    /// Serbest metin araması. Tarih aralığı verilmezse yalnızca tekrarlamayan
    /// etkinlikler ve serilerin pencere içindeki örnekleri döner.
    /// </summary>
    public async Task<List<EventOccurrence>> SearchAsync(
        string term,
        Instant fromInclusive,
        Instant toExclusive,
        OccurrenceFilter? filter = null,
        CancellationToken ct = default)
    {
        filter ??= new OccurrenceFilter();
        return await GetOccurrencesAsync(fromInclusive, toExclusive, filter with { SearchTerm = term }, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Çöp kutusundaki etkinlikler, en son silinen başta.</summary>
    public Task<List<Event>> GetTrashAsync(CancellationToken ct = default)
        => db.Events
            .AsNoTracking()
            .Include(e => e.Calendar)
            .Where(e => e.DeletedAt != null)
            .OrderByDescending(e => e.DeletedAt)
            .ToListAsync(ct);

    /// <summary>
    /// Belirli bir örneği bulur. Düzenleme ekranı, ızgaradan tıklanan örneği bununla açar.
    /// </summary>
    public async Task<EventOccurrence?> FindOccurrenceAsync(
        Guid eventId,
        LocalDateTime? recurrenceId,
        CancellationToken ct = default)
    {
        var ev = await db.Events
            .AsNoTracking()
            .Include(e => e.Calendar)
            .Include(e => e.Reminders)
            .Include(e => e.Categories)
            .FirstOrDefaultAsync(e => e.Id == eventId, ct).ConfigureAwait(false);

        if (ev is null) return null;

        // Tekrarlamayan etkinlik ya da doğrudan istisna satırı: tek örnek.
        if (recurrenceId is null || ev.RecurrenceRule is null)
        {
            return new EventOccurrence
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

        var exceptions = await db.Events
            .AsNoTracking()
            .Where(e => e.SeriesId == ev.Id && e.DeletedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);

        // Örneği tam olarak kendi aralığında ararız; bir günlük pencere yeterlidir.
        var target = recurrenceId.Value;
        var windowStart = ev.StartUtc;
        var probeFrom = Instant.FromDateTimeUtc(DateTime.SpecifyKind(
            target.PlusDays(-1).ToDateTimeUnspecified(), DateTimeKind.Utc));
        var probeTo = probeFrom + Duration.FromDays(3);
        if (probeFrom < windowStart) probeFrom = windowStart;

        return expander.Expand(ev, exceptions, probeFrom, probeTo)
            .FirstOrDefault(o => o.RecurrenceId == target);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Pencereye örnek üretebilecek kök satırları çeker.
    /// Tekrarlamayanlar aralık kesişimiyle, seriler ise başlangıç/seri sonu
    /// aralığıyla elenir; süresiz seriler her zaman aday kalır.
    /// </summary>
    private async Task<List<Event>> QueryRootsAsync(
        Instant from, Instant to, OccurrenceFilter filter, CancellationToken ct)
    {
        var query = db.Events
            .AsNoTracking()
            .Include(e => e.Calendar)
            .Include(e => e.Categories)
            .Where(e => e.DeletedAt == null && e.SeriesId == null);

        if (filter.CalendarIds.Count > 0)
        {
            var ids = filter.CalendarIds;
            query = query.Where(e => ids.Contains(e.CalendarId));
        }
        else
        {
            query = query.Where(e => e.Calendar!.IsVisible && e.Calendar.DeletedAt == null);
        }

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

    /// <summary>
    /// Örnek üretildikten sonra uygulanan süzgeçler. Kategori ve durum süzmesi
    /// istisna satırlarında kökten farklı olabileceği için burada yapılır.
    /// </summary>
    private static List<EventOccurrence> ApplyPostFilters(List<EventOccurrence> occurrences, OccurrenceFilter filter)
    {
        IEnumerable<EventOccurrence> result = occurrences;

        if (filter.CategoryIds.Count > 0)
        {
            var wanted = filter.CategoryIds.ToHashSet();
            result = result.Where(o => o.Source.Categories.Any(c => wanted.Contains(c.CategoryId)));
        }

        if (filter.Availabilities.Count > 0)
        {
            var wanted = filter.Availabilities.ToHashSet();
            result = result.Where(o => wanted.Contains(o.Source.Availability));
        }

        if (!filter.IncludeCancelled)
            result = result.Where(o => o.Source.Status != EventStatus.Cancelled);

        if (!string.IsNullOrWhiteSpace(filter.SearchTerm))
        {
            // Kök sorgusu seriyi metne göre elemiş olabilir; istisnalar ayrıca sınanır.
            var term = filter.SearchTerm;
            result = result.Where(o => !o.IsException || TurkishText.Contains(o.Source.SearchText, term));
        }

        return [.. result];
    }
}
