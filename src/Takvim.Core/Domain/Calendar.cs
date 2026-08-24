namespace Takvim.Core.Domain;

/// <summary>
/// Bir takvim koleksiyonu. Kullanıcının birden çok takvimi olabilir; her etkinlik
/// tam olarak bir takvime aittir. CalDAV'daki "calendar collection" karşılığıdır.
/// </summary>
public class Calendar
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid OwnerUserId { get; set; }
    public User? Owner { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Palet anahtarı (ör. "tomato") veya #RRGGBB.</summary>
    public string Color { get; set; } = "peacock";

    public CalendarKind Kind { get; set; } = CalendarKind.Personal;

    /// <summary>Kenar çubuğundaki onay kutusunun durumu.</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>Kenar çubuğu sıralaması.</summary>
    public int SortOrder { get; set; }

    /// <summary>Abone ve tatil takvimleri için düzenleme kapalıdır.</summary>
    public bool IsReadOnly { get; set; }

    /// <summary>Takvimin varsayılan zaman dilimi; etkinlik kendi dilimini belirtmezse kullanılır.</summary>
    public string TimeZoneId { get; set; } = "Europe/Istanbul";

    /// <summary>Zamanlı etkinlikler için varsayılan hatırlatıcı, dakika cinsinden. Null ise hatırlatıcı yok.</summary>
    public int? DefaultReminderMinutes { get; set; } = 10;

    /// <summary>Tüm gün etkinlikleri için ayrı varsayılan. Gün başlangıcından önceki dakika.</summary>
    public int? DefaultAllDayReminderMinutes { get; set; } = 12 * 60;

    // --- Abone takvimler ---
    // NOT: Bu iki alan ileriye dönük ayrılmıştır; dış ICS beslemesine abonelik
    // henüz uygulanmadı. Şu an hiçbir kod bunları okumuyor ya da yazmıyor.
    // Sütun eklemek şema göçü gerektirdiği için baştan tanımlandılar.

    /// <summary>Dış ICS beslemesinin adresi. Abonelik uygulanana kadar kullanılmaz.</summary>
    public string? SourceUrl { get; set; }

    /// <summary>Beslemenin en son ne zaman çekildiği. Abonelik uygulanana kadar kullanılmaz.</summary>
    public DateTimeOffset? LastSyncedAt { get; set; }

    // --- Senkronizasyon meta verisi ---

    /// <summary>CalDAV koleksiyon etiketi. Koleksiyondaki her değişiklikte artar.</summary>
    public long SyncToken { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Çöp kutusu. Null değilse takvim silinmiştir.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    public ICollection<Event> Events { get; set; } = [];
    public ICollection<CalendarShare> Shares { get; set; } = [];
}
