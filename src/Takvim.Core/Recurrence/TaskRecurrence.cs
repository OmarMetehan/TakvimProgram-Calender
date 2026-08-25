using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using NodaTime;

namespace Takvim.Core.Recurrence;

/// <summary>
/// Görevlerde tekrarın hesabı.
/// <para>
/// Görevler etkinliklerden farklı çalışır: seri önceden açılmaz. "Her pazartesi
/// rapor" görevi bir tanedir; tamamlandığında bir sonraki pazartesiye taşınır.
/// Aksi hâlde tatilden dönen biri geçmişte biriken onlarca tamamlanmamış görevle
/// karşılaşırdı — oysa yapmadığı şey <b>bir</b> iş, on iki değil.
/// </para>
/// </summary>
public static class TaskRecurrence
{
    /// <summary>Sonsuz döngüye karşı üst sınır; bozuk bir kural akışı kilitlemesin.</summary>
    private const int MaxSteps = 500;

    /// <summary>
    /// Kuralın <paramref name="after"/> tarihinden <b>sonraki</b> ilk tekrarı.
    /// Kural yoksa, çözümlenemezse ya da seri bitmişse null döner.
    /// </summary>
    public static LocalDate? Next(string? rule, LocalDate current, LocalDate after)
    {
        if (string.IsNullOrWhiteSpace(rule)) return null;

        RecurrencePattern pattern;
        try
        {
            pattern = new RecurrencePattern(rule);
        }
        catch (ArgumentException)
        {
            // Elle girilmiş bozuk kural; görev tekrarsız sayılır.
            return null;
        }

        var series = new CalendarEvent
        {
            DtStart = new CalDateTime(current.ToDateOnly()),
            DtEnd = new CalDateTime(current.PlusDays(1).ToDateOnly()),
            RecurrenceRule = pattern,
        };

        var seen = 0;

        foreach (var occurrence in series.GetOccurrences(new CalDateTime(current.ToDateOnly()), options: null))
        {
            if (++seen > MaxSteps) return null;

            var start = occurrence.Period.StartTime;
            var date = new LocalDate(start.Year, start.Month, start.Day);

            if (date > after) return date;
        }

        // COUNT ya da UNTIL ile biten bir seri: taşınacak yer yok.
        return null;
    }

    /// <summary>
    /// Tamamlanan görevin taşınacağı tarih.
    /// <para>
    /// Ölçü <b>tamamlanma günü</b> değil görevin kendi tarihidir: iki hafta geç
    /// tamamlanan haftalık bir görev, bir sonraki haftaya değil bugünden sonraki
    /// ilk tekrara gitmelidir, yoksa yine gecikmiş olarak doğar.
    /// </para>
    /// </summary>
    /// <param name="seriesStart">
    /// Serinin başladığı gün. <c>COUNT</c> buradan sayılır; görevin o anki
    /// tarihinden başlansaydı sayaç her taşımada sıfırlanır ve seri bitmezdi.
    /// </param>
    public static LocalDate? NextAfterCompletion(
        string? rule, LocalDate seriesStart, LocalDate dueDate, LocalDate today)
    {
        var reference = dueDate > today ? dueDate : today;

        return Next(rule, seriesStart, reference);
    }
}
