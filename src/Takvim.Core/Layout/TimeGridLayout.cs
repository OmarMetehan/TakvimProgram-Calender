using NodaTime;
using Takvim.Core.Recurrence;

namespace Takvim.Core.Layout;

/// <summary>
/// Izgarada tek bir etkinliğin yerleşimi. Ölçüler 0-1 arası oransal verilir;
/// piksele çevirmek görünümün işidir.
/// </summary>
public sealed record PlacedOccurrence(
    EventOccurrence Occurrence,
    /// <summary>Gün başlangıcından itibaren dikey konum, 0-1 arası.</summary>
    double Top,
    /// <summary>Gün yüksekliğine oranla yükseklik, 0-1 arası.</summary>
    double Height,
    /// <summary>Sütunun soldan başlangıcı, 0-1 arası.</summary>
    double Left,
    /// <summary>Sütun genişliği, 0-1 arası.</summary>
    double Width,
    /// <summary>Üst üste binen kartlarda çizim sırası; büyük olan üstte.</summary>
    int ZIndex);

/// <summary>
/// Gün sütununda çakışan etkinlikleri yan yana yerleştirir.
/// <para>
/// Çalışma biçimi: önce birbirine zincirleme değen etkinlikler bir <i>küme</i>
/// hâlinde toplanır, kümedeki her etkinliğe boş ilk sütun verilir, ardından
/// her etkinlik sağında yer varsa genişletilir. Bu son adım, üç etkinlikten
/// yalnızca ikisi çakıştığında üçüncünün gereksiz yere daralmasını önler.
/// </para>
/// </summary>
public static class TimeGridLayout
{
    /// <summary>
    /// Bir günün zamanlı etkinliklerini yerleştirir.
    /// </summary>
    /// <param name="occurrences">O güne düşen örnekler. Tüm gün etkinlikleri hariç tutulmalıdır.</param>
    /// <param name="dayStart">Günün ızgaradaki başlangıcı (genellikle 00:00).</param>
    /// <param name="dayEnd">Günün ızgaradaki bitişi (genellikle ertesi gün 00:00).</param>
    /// <param name="minimumHeight">Çok kısa etkinliklerin okunabilir kalması için asgari yükseklik oranı.</param>
    public static List<PlacedOccurrence> Place(
        IEnumerable<EventOccurrence> occurrences,
        LocalDateTime dayStart,
        LocalDateTime dayEnd,
        double minimumHeight = 0.015)
    {
        ArgumentNullException.ThrowIfNull(occurrences);

        var dayMinutes = Minutes(dayStart, dayEnd);
        if (dayMinutes <= 0) return [];

        var items = occurrences
            .Select(o => new Item(o,
                StartMinute: Math.Clamp(Minutes(dayStart, o.StartLocal), 0, dayMinutes),
                EndMinute: Math.Clamp(Minutes(dayStart, o.EndLocal), 0, dayMinutes)))
            .OrderBy(i => i.StartMinute)
            .ThenByDescending(i => i.EndMinute)
            .ToList();

        if (items.Count == 0) return [];

        var placed = new List<PlacedOccurrence>(items.Count);

        foreach (var cluster in BuildClusters(items, dayMinutes, minimumHeight))
        {
            var columns = AssignColumns(cluster, dayMinutes, minimumHeight);
            var columnCount = columns.Max(c => c.Column) + 1;

            foreach (var entry in columns)
            {
                // Sağa doğru boş sütun varsa kart oraya kadar genişler.
                var span = 1;
                while (entry.Column + span < columnCount
                       && !columns.Any(other => other.Column == entry.Column + span
                                                && Overlaps(other, entry, dayMinutes, minimumHeight)))
                {
                    span++;
                }

                var top = entry.Item.StartMinute / dayMinutes;
                var height = Math.Max((entry.Item.EndMinute - entry.Item.StartMinute) / dayMinutes, minimumHeight);
                if (top + height > 1) height = Math.Max(1 - top, minimumHeight);

                placed.Add(new PlacedOccurrence(
                    entry.Item.Occurrence,
                    Top: top,
                    Height: height,
                    Left: (double)entry.Column / columnCount,
                    Width: (double)span / columnCount,
                    // Dar kartlar geniş olanların üstünde kalmalı ki tıklanabilsinler.
                    ZIndex: entry.Column + 1));
            }
        }

        return placed;
    }

    /// <summary>Zincirleme çakışan etkinlikleri bir arada gruplar.</summary>
    private static List<List<Item>> BuildClusters(List<Item> items, double dayMinutes, double minimumHeight)
    {
        var clusters = new List<List<Item>>();
        var current = new List<Item> { items[0] };
        var clusterEnd = VisualEnd(items[0], dayMinutes, minimumHeight);

        foreach (var item in items.Skip(1))
        {
            if (item.StartMinute < clusterEnd)
            {
                current.Add(item);
                clusterEnd = Math.Max(clusterEnd, VisualEnd(item, dayMinutes, minimumHeight));
            }
            else
            {
                clusters.Add(current);
                current = [item];
                clusterEnd = VisualEnd(item, dayMinutes, minimumHeight);
            }
        }

        clusters.Add(current);
        return clusters;
    }

    /// <summary>Kümedeki her etkinliğe çakışmayan en soldaki sütunu verir.</summary>
    private static List<Entry> AssignColumns(List<Item> cluster, double dayMinutes, double minimumHeight)
    {
        var entries = new List<Entry>(cluster.Count);
        var columnEnds = new List<double>();

        foreach (var item in cluster)
        {
            var column = 0;
            while (column < columnEnds.Count && columnEnds[column] > item.StartMinute) column++;

            if (column == columnEnds.Count) columnEnds.Add(0);
            columnEnds[column] = VisualEnd(item, dayMinutes, minimumHeight);

            entries.Add(new Entry(item, column));
        }

        return entries;
    }

    private static bool Overlaps(Entry a, Entry b, double dayMinutes, double minimumHeight)
        => a.Item.StartMinute < VisualEnd(b.Item, dayMinutes, minimumHeight)
        && VisualEnd(a.Item, dayMinutes, minimumHeight) > b.Item.StartMinute;

    /// <summary>
    /// Etkinliğin ekranda kapladığı bitiş. Asgari yükseklik uygulandığı için
    /// çok kısa iki etkinlik gerçekte değmese bile görsel olarak çakışabilir.
    /// </summary>
    private static double VisualEnd(Item item, double dayMinutes, double minimumHeight)
        => Math.Max(item.EndMinute, item.StartMinute + minimumHeight * dayMinutes);

    private static double Minutes(LocalDateTime from, LocalDateTime to)
        => (to - from).ToDuration().TotalMinutes;

    private readonly record struct Item(EventOccurrence Occurrence, double StartMinute, double EndMinute);

    private readonly record struct Entry(Item Item, int Column);
}
