using Microsoft.EntityFrameworkCore;
using Takvim.Core.Domain;

namespace Takvim.Data.Services;

/// <summary>Geri alınabilir bir işlemin kullanıcıya gösterilen özeti.</summary>
public sealed record UndoEntry(Guid OperationId, string Description, DateTimeOffset At);

/// <summary>
/// Geri alma. Değişiklik günlüğündeki "önceki hal" kayıtlarını geri yazar.
/// <para>
/// Tek bir kullanıcı eylemi birden çok satıra dokunabilir ("bu ve sonrakiler"
/// düzenlemesi seriyi böler, iki satır yazar). Bu yüzden geri alma satır bazında
/// değil, <see cref="ChangeLogEntry.OperationId"/> bazında çalışır ve işlemin
/// tüm satırlarını ters sırayla çevirir.
/// </para>
/// </summary>
public sealed class UndoService(TakvimDbContext db)
{
    /// <summary>Son geri alınabilir işlemler, en yenisi başta.</summary>
    public async Task<List<UndoEntry>> GetRecentAsync(int count = 10, CancellationToken ct = default)
    {
        var groups = await db.ChangeLog
            .AsNoTracking()
            .Where(c => c.OperationId != Guid.Empty)
            .GroupBy(c => c.OperationId)
            .Select(g => new
            {
                OperationId = g.Key,
                At = g.Max(x => x.ChangedAt),
                Token = g.Min(x => x.SyncToken),
                Summary = g.OrderBy(x => x.SyncToken).First().Summary,
            })
            .OrderByDescending(g => g.Token)
            .Take(count)
            .ToListAsync(ct).ConfigureAwait(false);

        return [.. groups.Select(g => new UndoEntry(g.OperationId, g.Summary ?? "Değişiklik", g.At))];
    }

    /// <summary>
    /// Bir işlemi geri alır. İşlemin satırları ters sırayla çevrilir:
    /// oluşturulanlar silinir, silinenler ve değiştirilenler önceki hâline döner.
    /// </summary>
    /// <returns>Geri alma uygulandıysa true; işlem bulunamadıysa false.</returns>
    public async Task<bool> UndoAsync(Guid operationId, Guid? actorUserId = null, CancellationToken ct = default)
    {
        var entries = await db.ChangeLog
            .Where(c => c.OperationId == operationId && c.EntityType == nameof(Event))
            .OrderByDescending(c => c.SyncToken)
            .ToListAsync(ct).ConfigureAwait(false);

        if (entries.Count == 0) return false;

        var undoOperationId = Guid.NewGuid();
        var log = new ChangeLogWriter(db);

        foreach (var entry in entries)
        {
            var before = ChangeLogWriter.Deserialize(entry.BeforeJson);
            var existing = await db.Events
                .Include(e => e.Reminders)
                .Include(e => e.Categories)
                .FirstOrDefaultAsync(e => e.Id == entry.EntityId, ct).ConfigureAwait(false);

            if (before is null)
            {
                // Oluşturma işlemi geri alınıyor: satır kaldırılır.
                if (existing is not null)
                {
                    log.RecordEvent(undoOperationId, ChangeOperation.Delete, existing.Id, existing.CalendarId,
                        EventSnapshot.From(existing), after: null, actorUserId,
                        summary: "Geri alma: oluşturulan etkinlik kaldırıldı");
                    db.Events.Remove(existing);
                }
                continue;
            }

            if (existing is null)
            {
                // Kalıcı silinmiş satır geri getiriliyor.
                var restored = before.ToEvent();
                db.Events.Add(restored);
                RestoreChildren(restored, before);

                log.RecordEvent(undoOperationId, ChangeOperation.Restore, restored.Id, restored.CalendarId,
                    before: null, after: EventSnapshot.From(restored), actorUserId,
                    summary: "Geri alma: silinen etkinlik geri getirildi");
                continue;
            }

            var current = EventSnapshot.From(existing);
            before.ApplyTo(existing);
            RestoreChildren(existing, before);

            log.RecordEvent(undoOperationId, ChangeOperation.Update, existing.Id, existing.CalendarId,
                current, EventSnapshot.From(existing), actorUserId,
                summary: "Geri alma: " + (entry.Summary ?? "değişiklik çevrildi"));
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    private void RestoreChildren(Event ev, EventSnapshot snapshot)
    {
        db.Reminders.RemoveRange(ev.Reminders);
        ev.Reminders = [.. snapshot.Reminders.Select(r => new Reminder
        {
            EventId = ev.Id,
            MinutesBefore = r.MinutesBefore,
            Channel = r.Channel,
        })];

        db.EventCategories.RemoveRange(ev.Categories);
        ev.Categories = [.. snapshot.CategoryIds.Select(id => new EventCategory
        {
            EventId = ev.Id,
            CategoryId = id,
        })];
    }
}
