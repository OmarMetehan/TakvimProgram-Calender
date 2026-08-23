using NodaTime;

namespace Takvim.Core.Scheduling;

/// <summary>Bir katılımcının meşgul olduğu aralık.</summary>
/// <param name="Start">Başlangıç, dahil.</param>
/// <param name="End">Bitiş, hariç.</param>
/// <param name="Kind">Meşguliyetin türü; öneriler bunu ağırlıklandırır.</param>
public readonly record struct BusyInterval(Instant Start, Instant End, Domain.Availability Kind)
{
    public Duration Length => End - Start;

    public bool Overlaps(Instant from, Instant to) => Start < to && End > from;

    /// <summary>
    /// Bu aralık gerçekten engel mi. "Belirsiz" işaretli etkinlikler üzerine
    /// toplantı konabilir; öneri sıralamasında ceza alır ama elenmez.
    /// </summary>
    public bool IsBlocking => Kind is not (Domain.Availability.Free or Domain.Availability.Tentative);
}

/// <summary>Zamanlama ızgarasında tek bir satır: bir kişi ya da bir takvim.</summary>
/// <param name="Id">Satırın kimliği (kullanıcı ya da takvim).</param>
/// <param name="Name">Görünen ad.</param>
/// <param name="Busy">Meşgul aralıkları, sıralı ve birleştirilmiş.</param>
/// <param name="IsRequired">Zorunlu katılımcı mı; öneriler zorunluları önceler.</param>
/// <param name="WorkingHours">Kişinin o günkü mesaisi; null ise kısıt uygulanmaz.</param>
public sealed record ScheduleLane(
    Guid Id,
    string Name,
    IReadOnlyList<BusyInterval> Busy,
    bool IsRequired = true,
    (Instant Start, Instant End)? WorkingHours = null);

/// <summary>Önerilen bir toplantı aralığı.</summary>
/// <param name="Start">Aralığın başlangıcı.</param>
/// <param name="End">Aralığın bitişi.</param>
/// <param name="ConflictingRequired">Bu aralıkta meşgul olan zorunlu katılımcı sayısı.</param>
/// <param name="ConflictingOptional">Bu aralıkta meşgul olan isteğe bağlı katılımcı sayısı.</param>
/// <param name="OutsideWorkingHours">Aralık, katılımcılardan en az birinin mesaisi dışında mı.</param>
public sealed record SlotSuggestion(
    Instant Start,
    Instant End,
    int ConflictingRequired,
    int ConflictingOptional,
    bool OutsideWorkingHours)
{
    /// <summary>Kimse meşgul değil ve herkesin mesaisi içinde.</summary>
    public bool IsPerfect => ConflictingRequired == 0 && ConflictingOptional == 0 && !OutsideWorkingHours;

    /// <summary>Sıralama puanı; küçük olan daha iyidir.</summary>
    internal int Penalty =>
        ConflictingRequired * 100 + ConflictingOptional * 10 + (OutsideWorkingHours ? 5 : 0);
}

/// <summary>
/// Zamanlama yardımcısının çekirdeği: meşguliyet aralıklarını birleştirir ve
/// uygun toplantı zamanları önerir.
/// <para>
/// Hiçbir aralık tümüyle boş değilse öneri üretmemek yerine, en az çakışan
/// aralıklar sıralanır. Gerçekte kullanıcılar çoğu zaman "kimsenin boş olmadığı"
/// bir takvimde en az kötü seçeneği arar.
/// </para>
/// </summary>
public static class FreeBusy
{
    /// <summary>Üst üste binen ve bitişik aralıkları tek aralığa indirger.</summary>
    public static List<BusyInterval> Merge(IEnumerable<BusyInterval> intervals)
    {
        ArgumentNullException.ThrowIfNull(intervals);

        var sorted = intervals
            .Where(i => i.End > i.Start)
            .OrderBy(i => i.Start)
            .ToList();

        if (sorted.Count == 0) return [];

        var merged = new List<BusyInterval> { sorted[0] };

        foreach (var current in sorted.Skip(1))
        {
            var last = merged[^1];

            if (current.Start <= last.End)
            {
                // Birleşen aralıklarda daha engelleyici olan tür korunur.
                var kind = last.IsBlocking ? last.Kind : current.Kind;
                merged[^1] = new BusyInterval(last.Start, Max(last.End, current.End), kind);
            }
            else
            {
                merged.Add(current);
            }
        }

        return merged;
    }

    /// <summary>
    /// Verilen pencerede, istenen uzunlukta toplantı aralıkları önerir.
    /// </summary>
    /// <param name="lanes">Katılımcı satırları.</param>
    /// <param name="windowStart">Aranacak pencerenin başlangıcı.</param>
    /// <param name="windowEnd">Aranacak pencerenin bitişi.</param>
    /// <param name="duration">Toplantı uzunluğu.</param>
    /// <param name="step">Aday aralıkların adım genişliği; genellikle 15 ya da 30 dakika.</param>
    /// <param name="maxResults">Döndürülecek en fazla öneri sayısı.</param>
    public static List<SlotSuggestion> Suggest(
        IReadOnlyList<ScheduleLane> lanes,
        Instant windowStart,
        Instant windowEnd,
        Duration duration,
        Duration step,
        int maxResults = 12)
    {
        ArgumentNullException.ThrowIfNull(lanes);

        if (duration <= Duration.Zero || step <= Duration.Zero) return [];
        if (windowEnd - windowStart < duration) return [];

        var suggestions = new List<SlotSuggestion>();

        for (var start = windowStart; start + duration <= windowEnd; start += step)
        {
            var end = start + duration;
            var conflictingRequired = 0;
            var conflictingOptional = 0;
            var outsideWork = false;

            foreach (var lane in lanes)
            {
                if (lane.Busy.Any(b => b.IsBlocking && b.Overlaps(start, end)))
                {
                    if (lane.IsRequired) conflictingRequired++;
                    else conflictingOptional++;
                }

                if (lane.WorkingHours is { } hours && (start < hours.Start || end > hours.End))
                {
                    outsideWork = true;
                }
            }

            suggestions.Add(new SlotSuggestion(start, end, conflictingRequired, conflictingOptional, outsideWork));
        }

        // Önce en az çakışan, eşitlikte en erken aralık.
        return [.. suggestions
            .OrderBy(s => s.Penalty)
            .ThenBy(s => s.Start)
            .Take(maxResults)];
    }

    /// <summary>
    /// Tüm satırların ortak boş olduğu aralıklar. Öneri üretmek yerine
    /// "şu aralıklar tümüyle boş" demek gerektiğinde kullanılır.
    /// </summary>
    public static List<(Instant Start, Instant End)> CommonFreeIntervals(
        IReadOnlyList<ScheduleLane> lanes,
        Instant windowStart,
        Instant windowEnd,
        Duration minimumLength)
    {
        ArgumentNullException.ThrowIfNull(lanes);

        var blocking = Merge(lanes.SelectMany(l => l.Busy).Where(b => b.IsBlocking));
        var free = new List<(Instant Start, Instant End)>();
        var cursor = windowStart;

        foreach (var busy in blocking)
        {
            if (busy.Start > cursor)
            {
                var gapEnd = Min(busy.Start, windowEnd);
                if (gapEnd - cursor >= minimumLength) free.Add((cursor, gapEnd));
            }

            cursor = Max(cursor, busy.End);
            if (cursor >= windowEnd) break;
        }

        if (cursor < windowEnd && windowEnd - cursor >= minimumLength) free.Add((cursor, windowEnd));

        return free;
    }

    private static Instant Max(Instant a, Instant b) => a > b ? a : b;

    private static Instant Min(Instant a, Instant b) => a < b ? a : b;
}
