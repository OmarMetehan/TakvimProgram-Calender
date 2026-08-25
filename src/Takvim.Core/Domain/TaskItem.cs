using NodaTime;

namespace Takvim.Core.Domain;

/// <summary>Görevin durumu.</summary>
public enum TaskState
{
    /// <summary>Henüz başlanmadı.</summary>
    Todo = 0,

    /// <summary>Üzerinde çalışılıyor.</summary>
    InProgress = 1,

    /// <summary>Tamamlandı.</summary>
    Done = 2,
}

/// <summary>
/// Görev önceliği. Sayılar sıralamada kullanıldığı için düşük değer yüksek
/// önceliktir; "1. öncelik" alışkanlığıyla uyumlu olsun diye.
/// </summary>
public enum TaskPriority
{
    High = 0,
    Normal = 1,
    Low = 2,
}

/// <summary>
/// Görev listesi. Takvimden ayrı tutulur: bir görevin yeri "ne zaman
/// yapılacağı" değil "hangi listede olduğu"dur.
/// </summary>
public class TaskList
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid OwnerUserId { get; set; }

    public required string Name { get; set; }

    /// <summary>Palet anahtarı; takvim renkleriyle aynı isimler kullanılır.</summary>
    public string Color { get; set; } = "sage";

    public int SortOrder { get; set; }

    /// <summary>
    /// Hesap açılırken oluşturulan varsayılan liste. Silinemez: kullanıcı tüm
    /// listelerini silerse görev ekleyecek yeri kalmazdı.
    /// </summary>
    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<TaskItem> Tasks { get; set; } = [];
}

/// <summary>
/// Tek bir görev.
/// <para>
/// Bitiş tarihi <b>yerel</b> tutulur ve saati isteğe bağlıdır: "cuma gününe
/// kadar" ile "cuma 17:00'ye kadar" farklı şeylerdir, ve ikisi de kullanıcının
/// takviminde hangi zaman diliminde olduğuna bakılmaksızın aynı gün anlamına
/// gelir. Bu, etkinliklerdeki "yerel saat + IANA kimliği" kuralının görevlere
/// düşen hâlidir.
/// </para>
/// </summary>
public class TaskItem
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid TaskListId { get; set; }
    public TaskList? List { get; set; }

    /// <summary>Alt görevse üst görevin kimliği.</summary>
    public Guid? ParentTaskId { get; set; }
    public TaskItem? Parent { get; set; }
    public List<TaskItem> Subtasks { get; set; } = [];

    public required string Title { get; set; }

    /// <summary>Serbest metin not; açıklamalarla aynı biçimlendirme kurallarını izler.</summary>
    public string? Notes { get; set; }

    /// <summary>Bitiş günü. Null ise görevin tarihi yoktur.</summary>
    public LocalDate? DueDate { get; set; }

    /// <summary>Bitiş saati. Null ise görev "gün içinde" demektir.</summary>
    public LocalTime? DueTime { get; set; }

    public TaskState State { get; set; } = TaskState.Todo;
    public TaskPriority Priority { get; set; } = TaskPriority.Normal;

    /// <summary>Tamamlanma anı; "bugün bitirdiklerim" bunun üzerinden bulunur.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// RFC 5545 tekrar kuralı. Görevlerde tekrar, etkinliklerden farklı işler:
    /// seri önceden üretilmez, görev tamamlandığında bir sonraki tarihe taşınır.
    /// Böylece "her pazartesi rapor" görevi geçmişte biriken onlarca satır
    /// bırakmaz.
    /// </summary>
    public string? RecurrenceRule { get; set; }

    /// <summary>
    /// Serinin başladığı gün. Tekrar hesabı her seferinde buradan yapılır:
    /// görevin o anki tarihinden başlanırsa <c>COUNT=3</c> gibi kurallar her
    /// taşımada baştan sayar ve seri hiç bitmez.
    /// </summary>
    public LocalDate? RecurrenceStartDate { get; set; }

    /// <summary>Listedeki elle verilmiş sıra.</summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Göreve zaman ayırmak için takvime konan etkinlik. Görev silinince
    /// etkinlik de silinir; etkinlik silinirse bağ kopar ve görev kalır.
    /// </summary>
    public Guid? ScheduledEventId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Çöp kutusu; etkinliklerle aynı davranış.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>Aramada kullanılan normalleştirilmiş metin.</summary>
    public string SearchText { get; set; } = string.Empty;

    // ------------------------------------------------------------------

    public bool IsDone => State == TaskState.Done;

    /// <summary>Bitiş anı; saat verilmemişse günün sonu sayılır.</summary>
    public LocalDateTime? Due => DueDate is { } date
        ? date.At(DueTime ?? new LocalTime(23, 59))
        : null;

    /// <summary>Verilen güne göre gecikmiş mi. Tamamlanmış görev gecikmez.</summary>
    public bool IsOverdue(LocalDateTime now)
        => !IsDone && Due is { } due && due < now;

    /// <summary>Verilen gün bu görevin bitiş günü mü.</summary>
    public bool IsDueOn(LocalDate date) => DueDate == date;
}
