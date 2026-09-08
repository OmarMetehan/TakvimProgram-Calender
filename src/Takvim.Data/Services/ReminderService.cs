using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Recurrence;

namespace Takvim.Data.Services;

/// <summary>Zamanı gelmiş bir hatırlatıcı.</summary>
/// <param name="ReminderId">Erteleme ve kapatma bu kimlikle yapılır.</param>
/// <param name="OccurrenceStartUtc">Hatırlatılan örneğin başlangıcı; tekrarlayan seride hangi örnek olduğunu ayırır.</param>
public sealed record DueReminder(
    Guid ReminderId,
    Guid EventId,
    LocalDateTime? RecurrenceId,
    Instant OccurrenceStartUtc,
    LocalDateTime StartLocal,
    LocalDateTime EndLocal,
    bool IsAllDay,
    string Title,
    string? LocationText,
    string Color,
    int MinutesBefore);

/// <summary>
/// Hatırlatıcı motoru. Zamanı gelmiş hatırlatıcıları bulur, erteler ve kapatır.
/// <para>
/// Tekrarlayan etkinliklerde tek bir hatırlatıcı satırı sonsuz sayıda örneğe
/// hizmet eder. Aynı örnek için ikinci kez çalmayı önlemek üzere en son hangi
/// örnek için tetiklendiği <see cref="Reminder.LastFiredForOccurrenceAt"/>
/// alanında tutulur.
/// </para>
/// <para>
/// Hatırlatıcılar takvim sahibine çalar. Aynı makinede birden çok yerel hesap
/// olabildiği için sorgu her zaman bir kullanıcıya bağlıdır: paylaşılan bir
/// takvimin uyarısı, o takvimi görebilen herkesin ekranında değil sahibinin
/// ekranında çıkar.
/// </para>
/// </summary>
public sealed class ReminderService(
    TakvimDbContext db,
    RecurrenceExpander expander,
    IClock clock,
    NotificationSettingsService notifications)
{
    /// <summary>
    /// Uygulama kapalıyken geçen hatırlatıcılar açılışta topluca çalmasın diye
    /// konan sınır: başlangıcının üzerinden bu süreden fazla geçmişse atlanır.
    /// </summary>
    public static readonly Duration MissedGrace = Duration.FromMinutes(30);

    /// <summary>Bir hatırlatıcının etkinlikten en fazla ne kadar önce kurulabileceği.</summary>
    private static readonly Duration MaxLeadTime = Duration.FromDays(30);

    /// <summary>
    /// Verilen kullanıcının zamanı gelmiş hatırlatıcılarını döner. Tetiklendi
    /// olarak işaretlemez.
    /// <para>
    /// Bildirimler kapalıysa ya da sessiz saatlerdeysek liste boş döner:
    /// hatırlatıcı satırlarına dokunulmaz, yalnızca gösterilmez. Sessizlik
    /// bitince aynı hatırlatıcı — hâlâ zamanındaysa — bir sonraki turda çalar.
    /// </para>
    /// </summary>
    public async Task<List<DueReminder>> GetDueAsync(Guid userId, CancellationToken ct = default)
    {
        var silence = await notifications.EvaluateAsync(userId, ct).ConfigureAwait(false);
        if (silence.IsSilenced) return [];

        var now = clock.GetCurrentInstant();
        var horizon = now + MaxLeadTime;

        var reminders = await db.Reminders
            .AsNoTracking()
            .Include(r => r.Event!).ThenInclude(e => e.Calendar)
            .Where(r => r.Event!.DeletedAt == null
                        && r.Event.Status != EventStatus.Cancelled
                        && r.Event.Calendar!.DeletedAt == null
                        && r.Event.Calendar.OwnerUserId == userId
                        // Seri hâlâ sürüyorsa ya da tekil etkinlik ufuktan önceyse aday.
                        && (r.Event.SeriesEndUtc == null || r.Event.SeriesEndUtc > now - MaxLeadTime)
                        && r.Event.StartUtc < horizon)
            .ToListAsync(ct).ConfigureAwait(false);

        if (reminders.Count == 0) return [];

        // İstisna satırları kendi hatırlatıcılarını taşır; seri genişletmesinde
        // yerlerini alabilmeleri için ayrıca yüklenir.
        var seriesIds = reminders
            .Select(r => r.Event!.Id)
            .Distinct()
            .ToList();

        var exceptions = await db.Events
            .AsNoTracking()
            .Where(e => e.SeriesId != null && seriesIds.Contains(e.SeriesId.Value) && e.DeletedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);

        var exceptionsBySeries = exceptions.ToLookup(e => e.SeriesId!.Value);
        var due = new List<DueReminder>();

        foreach (var reminder in reminders)
        {
            var ev = reminder.Event!;

            // Ertelenmiş hatırlatıcı, erteleme süresi dolana kadar sessizdir.
            if (reminder.SnoozedUntil is { } snoozedUntil
                && Instant.FromDateTimeOffset(snoozedUntil) > now)
            {
                continue;
            }

            var lead = Duration.FromMinutes(reminder.MinutesBefore);

            // Tetikleme anı örnek başlangıcından "lead" kadar öncedir; bu yüzden
            // şimdi tetiklenecek örnekler [now, now + lead] aralığında başlar.
            var searchFrom = now - MissedGrace;
            var searchTo = now + lead + Duration.FromMinutes(1);

            foreach (var occurrence in expander.Expand(ev, [.. exceptionsBySeries[ev.Id]], searchFrom, searchTo))
            {
                var triggerAt = occurrence.StartUtc - lead;

                if (triggerAt > now) continue;                       // henüz zamanı gelmedi
                if (occurrence.StartUtc + MissedGrace < now) continue; // çok geçmişte kaldı

                // Bu örnek için zaten çaldıysa tekrar çalmaz.
                if (reminder.LastFiredForOccurrenceAt is { } lastFired
                    && Instant.FromDateTimeOffset(lastFired) >= occurrence.StartUtc)
                {
                    continue;
                }

                due.Add(new DueReminder(
                    reminder.Id,
                    occurrence.EventId,
                    occurrence.RecurrenceId,
                    occurrence.StartUtc,
                    occurrence.StartLocal,
                    occurrence.EndLocal,
                    occurrence.IsAllDay,
                    string.IsNullOrWhiteSpace(occurrence.Source.Title) ? "(başlıksız)" : occurrence.Source.Title,
                    occurrence.Source.LocationText,
                    occurrence.Source.Color ?? ev.Calendar?.Color ?? "peacock",
                    reminder.MinutesBefore));

                break; // Bir hatırlatıcı, bir turda en fazla bir örnek için çalar.
            }
        }

        return [.. due.OrderBy(d => d.OccurrenceStartUtc)];
    }

    /// <summary>Hatırlatıcıyı bu örnek için tetiklendi olarak işaretler; bir daha çalmaz.</summary>
    public async Task MarkFiredAsync(Guid reminderId, Instant occurrenceStartUtc, CancellationToken ct = default)
    {
        var reminder = await db.Reminders.FirstOrDefaultAsync(r => r.Id == reminderId, ct).ConfigureAwait(false);
        if (reminder is null) return;

        reminder.LastFiredForOccurrenceAt = occurrenceStartUtc.ToDateTimeOffset();
        reminder.SnoozedUntil = null;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Hatırlatıcıyı belirtilen süre kadar erteler.</summary>
    public async Task SnoozeAsync(Guid reminderId, Duration delay, CancellationToken ct = default)
    {
        var reminder = await db.Reminders.FirstOrDefaultAsync(r => r.Id == reminderId, ct).ConfigureAwait(false);
        if (reminder is null) return;

        reminder.SnoozedUntil = (clock.GetCurrentInstant() + delay).ToDateTimeOffset();

        // Erteleme, "bu örnek için çaldı" işaretini geri alır ki süre dolunca yeniden çalsın.
        reminder.LastFiredForOccurrenceAt = null;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Görünen hatırlatıcıların tamamını kapatır.</summary>
    public async Task DismissAllAsync(IEnumerable<DueReminder> reminders, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reminders);

        foreach (var due in reminders)
        {
            var reminder = await db.Reminders
                .FirstOrDefaultAsync(r => r.Id == due.ReminderId, ct).ConfigureAwait(false);

            if (reminder is null) continue;

            reminder.LastFiredForOccurrenceAt = due.OccurrenceStartUtc.ToDateTimeOffset();
            reminder.SnoozedUntil = null;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
