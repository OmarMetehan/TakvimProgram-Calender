using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Recurrence;
using Takvim.Core.Text;
using Takvim.Core.Time;

namespace Takvim.Data.Services;

/// <summary>Görev oluşturma ve güncelleme girdisi.</summary>
public sealed record TaskInput
{
    public required Guid TaskListId { get; init; }
    public required string Title { get; init; }

    public string? Notes { get; init; }

    public LocalDate? DueDate { get; init; }
    public LocalTime? DueTime { get; init; }

    public TaskState State { get; init; } = TaskState.Todo;
    public TaskPriority Priority { get; init; } = TaskPriority.Normal;

    public string? RecurrenceRule { get; init; }

    /// <summary>Alt görev olarak açılacaksa üst görevin kimliği.</summary>
    public Guid? ParentTaskId { get; init; }
}

/// <summary>Görev listesi görünümünün süzgeci.</summary>
public sealed record TaskFilter
{
    /// <summary>Boşsa kullanıcının tüm listeleri.</summary>
    public IReadOnlyList<Guid> TaskListIds { get; init; } = [];

    /// <summary>True ise tamamlananlar da gelir.</summary>
    public bool IncludeDone { get; init; }

    /// <summary>Verilirse yalnızca bu güne kadar (dahil) biten görevler.</summary>
    public LocalDate? DueThrough { get; init; }

    /// <summary>True ise yalnızca tarihi olmayan görevler.</summary>
    public bool? Undated { get; init; }

    public string? SearchTerm { get; init; }
}

/// <summary>
/// Görevler.
/// <para>
/// Görevler etkinliklerden ayrı bir dünyadır ve bilerek öyle bırakılmıştır:
/// bir görevin "ne zaman yapılacağı" değil "bitmesi gereken gün"ü vardır,
/// süresi yoktur, çakışması yoktur. Takvime yalnızca <see cref="ScheduleAsync"/>
/// ile açıkça yer ayrıldığında girer.
/// </para>
/// </summary>
public sealed class TaskService(TakvimDbContext db, IClock clock, TimeZoneService zones)
{
    /// <summary>Silinen görevin çöp kutusunda kalma süresi.</summary>
    public static readonly Duration TrashRetention = Duration.FromDays(30);

    // ==================================================================
    // Listeler
    // ==================================================================

    public Task<List<TaskList>> GetListsAsync(Guid userId, CancellationToken ct = default)
        => db.TaskLists
            .AsNoTracking()
            .Where(l => l.OwnerUserId == userId)
            .OrderByDescending(l => l.IsDefault)
            .ThenBy(l => l.SortOrder)
            .ThenBy(l => l.Name)
            .ToListAsync(ct);

    /// <summary>
    /// Kullanıcının varsayılan listesi; yoksa açılır. Görev eklemek isteyen
    /// biri, önce liste açmak zorunda kalmamalı.
    /// </summary>
    public async Task<TaskList> GetOrCreateDefaultListAsync(Guid userId, CancellationToken ct = default)
    {
        var existing = await db.TaskLists
            .FirstOrDefaultAsync(l => l.OwnerUserId == userId && l.IsDefault, ct).ConfigureAwait(false);

        if (existing is not null) return existing;

        var list = new TaskList
        {
            OwnerUserId = userId,
            Name = "Görevlerim",
            IsDefault = true,
            CreatedAt = clock.GetCurrentInstant().ToDateTimeOffset(),
        };

        db.TaskLists.Add(list);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return list;
    }

    public async Task<TaskList> CreateListAsync(
        Guid userId, string name, string color = "sage", CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var order = await db.TaskLists
            .Where(l => l.OwnerUserId == userId)
            .CountAsync(ct).ConfigureAwait(false);

        var list = new TaskList
        {
            OwnerUserId = userId,
            Name = name.Trim(),
            Color = color,
            SortOrder = order,
            CreatedAt = clock.GetCurrentInstant().ToDateTimeOffset(),
        };

        db.TaskLists.Add(list);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return list;
    }

    public async Task RenameListAsync(
        Guid listId, string name, string? color = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var list = await db.TaskLists.FirstOrDefaultAsync(l => l.Id == listId, ct).ConfigureAwait(false);
        if (list is null) return;

        list.Name = name.Trim();
        if (color is not null) list.Color = color;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Listeyi ve içindeki görevleri siler. Varsayılan liste silinmez: kullanıcı
    /// tüm listelerini silerse görev ekleyecek yeri kalmazdı.
    /// </summary>
    /// <returns>Silindiyse null, silinemediyse gerekçesi.</returns>
    public async Task<string?> DeleteListAsync(Guid listId, CancellationToken ct = default)
    {
        var list = await db.TaskLists.FirstOrDefaultAsync(l => l.Id == listId, ct).ConfigureAwait(false);

        if (list is null) return "Liste bulunamadı.";
        if (list.IsDefault) return "Varsayılan liste silinemez.";

        db.TaskLists.Remove(list);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return null;
    }

    // ==================================================================
    // Görevler
    // ==================================================================

    /// <summary>
    /// Görevleri okur. Sıralama sabittir: önce gecikmişler, sonra tarihliler
    /// tarihine göre, sonra tarihsizler; her grupta öncelik ve elle verilmiş
    /// sıra belirler.
    /// </summary>
    public async Task<List<TaskItem>> GetAsync(
        Guid userId, TaskFilter? filter = null, CancellationToken ct = default)
    {
        filter ??= new TaskFilter();

        var query = db.Tasks
            .AsNoTracking()
            .Include(t => t.Subtasks.Where(s => s.DeletedAt == null))
            .Where(t => t.List!.OwnerUserId == userId && t.DeletedAt == null && t.ParentTaskId == null);

        if (filter.TaskListIds.Count > 0)
        {
            var wanted = filter.TaskListIds.ToList();
            query = query.Where(t => wanted.Contains(t.TaskListId));
        }

        if (!filter.IncludeDone) query = query.Where(t => t.State != TaskState.Done);

        if (filter.DueThrough is { } through)
            query = query.Where(t => t.DueDate != null && t.DueDate <= through);

        if (filter.Undated is { } undated)
            query = undated ? query.Where(t => t.DueDate == null) : query.Where(t => t.DueDate != null);

        if (!string.IsNullOrWhiteSpace(filter.SearchTerm))
        {
            var normalized = TurkishText.Normalize(filter.SearchTerm);
            query = query.Where(t => t.SearchText.Contains(normalized));
        }

        var tasks = await query.ToListAsync(ct).ConfigureAwait(false);

        return Sort(tasks, Today(userId: null));
    }

    /// <summary>Bir günün görevleri; ızgaradaki tüm gün şeridi bunu kullanır.</summary>
    public Task<List<TaskItem>> GetForDateRangeAsync(
        Guid userId, LocalDate fromInclusive, LocalDate toExclusive, CancellationToken ct = default)
        => db.Tasks
            .AsNoTracking()
            .Where(t => t.List!.OwnerUserId == userId
                        && t.DeletedAt == null
                        && t.DueDate != null
                        && t.DueDate >= fromInclusive
                        && t.DueDate < toExclusive)
            .OrderBy(t => t.DueDate)
            .ThenBy(t => t.DueTime)
            .ThenBy(t => t.Priority)
            .ToListAsync(ct);

    public Task<TaskItem?> FindAsync(Guid taskId, CancellationToken ct = default)
        => db.Tasks
            .AsNoTracking()
            .Include(t => t.Subtasks.Where(s => s.DeletedAt == null))
            .FirstOrDefaultAsync(t => t.Id == taskId, ct);

    public async Task<TaskItem> CreateAsync(TaskInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Title);

        var order = await db.Tasks
            .Where(t => t.TaskListId == input.TaskListId && t.ParentTaskId == input.ParentTaskId)
            .CountAsync(ct).ConfigureAwait(false);

        var now = clock.GetCurrentInstant().ToDateTimeOffset();

        var task = new TaskItem
        {
            TaskListId = input.TaskListId,
            ParentTaskId = input.ParentTaskId,
            Title = input.Title.Trim(),
            SortOrder = order,
            CreatedAt = now,
            UpdatedAt = now,
        };

        ApplyInput(task, input);

        db.Tasks.Add(task);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return task;
    }

    public async Task<TaskItem?> UpdateAsync(Guid taskId, TaskInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Title);

        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct).ConfigureAwait(false);
        if (task is null) return null;

        task.TaskListId = input.TaskListId;
        task.Title = input.Title.Trim();

        ApplyInput(task, input);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return task;
    }

    /// <summary>
    /// Görevi tamamlar ya da tamamlanmasını geri alır.
    /// <para>
    /// Tekrarlayan bir görev tamamlandığında kapanmaz, bir sonraki tarihe
    /// taşınır ve yeniden açılır: yapılan iş o günün işiydi, görevin kendisi
    /// devam eder. Seri bittiyse (COUNT/UNTIL) görev normal biçimde kapanır.
    /// </para>
    /// </summary>
    public async Task<TaskItem?> SetDoneAsync(
        Guid taskId, bool done, Guid userId, CancellationToken ct = default)
    {
        var task = await db.Tasks
            .Include(t => t.Subtasks)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct).ConfigureAwait(false);

        if (task is null) return null;

        var now = clock.GetCurrentInstant().ToDateTimeOffset();
        task.UpdatedAt = now;

        if (!done)
        {
            task.State = TaskState.Todo;
            task.CompletedAt = null;

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return task;
        }

        // Tekrarlayan görev: kapanmaz, ileri taşınır.
        if (task.RecurrenceRule is not null && task.DueDate is { } dueDate)
        {
            var next = TaskRecurrence.NextAfterCompletion(
                task.RecurrenceRule, task.RecurrenceStartDate ?? dueDate, dueDate, Today(userId));

            if (next is { } nextDate)
            {
                task.DueDate = nextDate;
                task.State = TaskState.Todo;
                task.CompletedAt = null;

                // Alt görevler yeni tur için sıfırlanır; bu turun işaretleri
                // bir sonraki turda anlamsızdır.
                foreach (var subtask in task.Subtasks)
                {
                    subtask.State = TaskState.Todo;
                    subtask.CompletedAt = null;
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return task;
            }
        }

        task.State = TaskState.Done;
        task.CompletedAt = now;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return task;
    }

    /// <summary>Görevi çöp kutusuna atar; alt görevleri de birlikte gider.</summary>
    public async Task DeleteAsync(Guid taskId, CancellationToken ct = default)
    {
        var task = await db.Tasks
            .Include(t => t.Subtasks)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct).ConfigureAwait(false);

        if (task is null) return;

        var now = clock.GetCurrentInstant().ToDateTimeOffset();

        task.DeletedAt = now;
        foreach (var subtask in task.Subtasks) subtask.DeletedAt = now;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RestoreAsync(Guid taskId, CancellationToken ct = default)
    {
        var task = await db.Tasks
            .Include(t => t.Subtasks)
            .FirstOrDefaultAsync(t => t.Id == taskId, ct).ConfigureAwait(false);

        if (task is null) return;

        task.DeletedAt = null;
        foreach (var subtask in task.Subtasks) subtask.DeletedAt = null;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Saklama süresi dolan görevleri kalıcı siler.</summary>
    public async Task<int> PurgeTrashAsync(CancellationToken ct = default)
    {
        var cutoff = clock.GetCurrentInstant().Minus(TrashRetention).ToDateTimeOffset();

        return await db.Tasks
            .Where(t => t.DeletedAt != null && t.DeletedAt < cutoff)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Görevi listede yeniden konumlandırır.</summary>
    public async Task ReorderAsync(
        Guid taskId, Guid targetListId, int newIndex, CancellationToken ct = default)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct).ConfigureAwait(false);
        if (task is null) return;

        var siblings = await db.Tasks
            .Where(t => t.TaskListId == targetListId
                        && t.ParentTaskId == task.ParentTaskId
                        && t.DeletedAt == null
                        && t.Id != taskId)
            .OrderBy(t => t.SortOrder)
            .ToListAsync(ct).ConfigureAwait(false);

        task.TaskListId = targetListId;
        siblings.Insert(Math.Clamp(newIndex, 0, siblings.Count), task);

        for (var i = 0; i < siblings.Count; i++) siblings[i].SortOrder = i;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // ==================================================================
    // Takvime yer ayırma
    // ==================================================================

    /// <summary>
    /// Göreve takvimde yer ayırır: görevin kendisi listede kalır, takvime onun
    /// için bir etkinlik konur ve ikisi birbirine bağlanır. Görev zaten
    /// zamanlanmışsa var olan etkinlik taşınır, ikincisi açılmaz.
    /// </summary>
    public async Task<Guid?> ScheduleAsync(
        Guid taskId,
        Guid calendarId,
        LocalDateTime start,
        int durationMinutes,
        string zoneId,
        Guid actorUserId,
        EventService events,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct).ConfigureAwait(false);
        if (task is null) return null;

        var input = new EventInput
        {
            CalendarId = calendarId,
            Title = task.Title,
            DescriptionHtml = task.Notes,
            StartLocal = start,
            EndLocal = start.PlusMinutes(Math.Max(5, durationMinutes)),
            StartTimeZoneId = zoneId,
            EndTimeZoneId = zoneId,
            Availability = Availability.Busy,
            ActorUserId = actorUserId,
        };

        // Var olan blok taşınır; her zamanlamada yeni etkinlik açmak takvimi
        // aynı görevin kopyalarıyla doldururdu.
        if (task.ScheduledEventId is { } existingId
            && await db.Events.AnyAsync(e => e.Id == existingId && e.DeletedAt == null, ct).ConfigureAwait(false))
        {
            await events.UpdateAsync(existingId, null, SeriesEditScope.AllInSeries, input, ct)
                .ConfigureAwait(false);

            return existingId;
        }

        var created = await events.CreateAsync(input, ct).ConfigureAwait(false);

        task.ScheduledEventId = created.PrimaryEventId;
        task.UpdatedAt = clock.GetCurrentInstant().ToDateTimeOffset();

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return created.PrimaryEventId;
    }

    /// <summary>Takvimdeki bloğu kaldırır; görev listede kalır.</summary>
    public async Task UnscheduleAsync(
        Guid taskId, Guid actorUserId, EventService events, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct).ConfigureAwait(false);
        if (task?.ScheduledEventId is not { } eventId) return;

        task.ScheduledEventId = null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await events.DeleteAsync(eventId, null, SeriesEditScope.AllInSeries, actorUserId, ct)
            .ConfigureAwait(false);
    }

    // ==================================================================

    private void ApplyInput(TaskItem task, TaskInput input)
    {
        task.Notes = input.Notes;
        task.DueDate = input.DueDate;
        task.DueTime = input.DueDate is null ? null : input.DueTime;
        task.Priority = input.Priority;

        var rule = string.IsNullOrWhiteSpace(input.RecurrenceRule) ? null : input.RecurrenceRule;

        // Kural değişince seri baştan başlar; eski kuralın sayacı yeni kuralı
        // bağlamaz.
        if (rule != task.RecurrenceRule || task.RecurrenceStartDate is null)
        {
            task.RecurrenceStartDate = rule is null ? null : task.DueDate;
        }

        task.RecurrenceRule = rule;

        // Tarihi olmayan görev tekrarlayamaz: neyin tekrarlayacağı belirsiz olurdu.
        if (task.DueDate is null)
        {
            task.RecurrenceRule = null;
            task.RecurrenceStartDate = null;
        }

        var now = clock.GetCurrentInstant().ToDateTimeOffset();

        if (input.State != task.State)
        {
            task.State = input.State;
            task.CompletedAt = input.State == TaskState.Done ? now : null;
        }

        task.UpdatedAt = now;
        task.SearchText = TurkishText.BuildSearchText(task.Title, task.Notes);
    }

    /// <summary>Kullanıcının bulunduğu zaman dilimindeki bugün.</summary>
    private LocalDate Today(Guid? userId)
    {
        var zoneId = userId is null
            ? TimeZoneService.DefaultZoneId
            : db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.TimeZoneId)
                .FirstOrDefault() ?? TimeZoneService.DefaultZoneId;

        return zones.ToLocal(clock.GetCurrentInstant(), zoneId).Date;
    }

    /// <summary>
    /// Görünüm sırası: gecikmişler önce, sonra tarihliler, en sonda tarihsizler.
    /// Sıralama veritabanında yapılamaz çünkü "gecikmiş" tanımı bugüne bağlıdır.
    /// </summary>
    private static List<TaskItem> Sort(List<TaskItem> tasks, LocalDate today)
        => [.. tasks
            .OrderBy(t => t.IsDone ? 1 : 0)
            .ThenBy(t => Bucket(t, today))
            .ThenBy(t => t.DueDate ?? LocalDate.MaxIsoValue)
            .ThenBy(t => t.DueTime ?? LocalTime.MaxValue)
            .ThenBy(t => t.Priority)
            .ThenBy(t => t.SortOrder)];

    private static int Bucket(TaskItem task, LocalDate today) => task.DueDate switch
    {
        null => 2,
        var date when date < today => 0,
        _ => 1,
    };
}
