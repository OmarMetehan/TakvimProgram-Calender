using NodaTime;

namespace Takvim.Core.Domain;

/// <summary>
/// Bir takvim etkinliği. Üç farklı rolde olabilir:
/// <list type="bullet">
/// <item>Tekil etkinlik: <see cref="RecurrenceRule"/> ve <see cref="RecurrenceId"/> boş.</item>
/// <item>Seri kökü: <see cref="RecurrenceRule"/> dolu, <see cref="RecurrenceId"/> boş.</item>
/// <item>Örnek istisnası: <see cref="RecurrenceId"/> dolu; seriden bir örneğin yerine geçer.</item>
/// </list>
/// Bu ayrım RFC 5545'in modeliyle birebir aynıdır: seri kökü ve istisnalar aynı
/// <see cref="Uid"/> değerini paylaşır, istisnalar RECURRENCE-ID ile ayrışır.
/// Böylece CalDAV ve ICS dışa aktarımı dönüştürme gerektirmez.
/// </summary>
public class Event
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid CalendarId { get; set; }
    public Calendar? Calendar { get; set; }

    /// <summary>
    /// iCalendar UID. Seri kökü ve tüm istisnaları aynı değeri taşır.
    /// Dış sistemlerle senkronizasyonda etkinliğin kalıcı kimliğidir; asla değişmez.
    /// </summary>
    public required string Uid { get; set; }

    // ------------------------------------------------------------------
    // Zaman. UTC olarak DEĞİL, yerel saat + IANA zaman dilimi olarak saklanır.
    // Aksi halde bir ülke yaz saati kuralını değiştirdiğinde kayıtlı etkinlikler kayar.
    // *Utc alanları yalnızca sorgu hızlandırma amaçlı türetilmiş önbellektir ve
    // zaman dilimi veritabanı güncellendiğinde yeniden hesaplanır.
    // ------------------------------------------------------------------

    /// <summary>Yerel başlangıç saati. Tüm gün etkinliklerinde saat kısmı 00:00'dır.</summary>
    public LocalDateTime StartLocal { get; set; }

    /// <summary>
    /// Yerel bitiş saati. iCalendar DTEND gibi <b>dışlayıcıdır</b>: 09:00-10:00 toplantı
    /// için 10:00, tek günlük tüm gün etkinliği için ertesi günün 00:00'ıdır.
    /// </summary>
    public LocalDateTime EndLocal { get; set; }

    /// <summary>
    /// Başlangıcın IANA zaman dilimi, ör. "Europe/Istanbul".
    /// Tüm gün etkinliklerinde null'dur (kayan tarih, her dilimde aynı gün görünür).
    /// </summary>
    public string? StartTimeZoneId { get; set; }

    /// <summary>
    /// Bitişin IANA zaman dilimi. Başlangıçtan farklı olabilir: uçuşlar gibi
    /// iki dilim arasında geçen etkinlikler için ayrı tutulur.
    /// </summary>
    public string? EndTimeZoneId { get; set; }

    public bool IsAllDay { get; set; }

    /// <summary>Türetilmiş önbellek: aralık sorguları ve çakışma denetimi bunun üzerinden çalışır.</summary>
    public Instant StartUtc { get; set; }

    /// <summary>Türetilmiş önbellek. Bkz. <see cref="StartUtc"/>.</summary>
    public Instant EndUtc { get; set; }

    // ------------------------------------------------------------------
    // İçerik
    // ------------------------------------------------------------------

    public string Title { get; set; } = string.Empty;

    /// <summary>Zengin metin açıklama; arındırılmış HTML olarak saklanır.</summary>
    public string? DescriptionHtml { get; set; }

    public string? LocationText { get; set; }

    public string? OnlineMeetingUrl { get; set; }

    /// <summary>Sağlayıcı anahtarı, ör. "teams", "meet", "zoom".</summary>
    public string? OnlineMeetingProvider { get; set; }

    /// <summary>Etkinliğe özel renk. Null ise takvimin rengi kullanılır.</summary>
    public string? Color { get; set; }

    /// <summary>
    /// Arama için normalleştirilmiş metin: başlık, açıklama, konum ve katılımcılar
    /// küçük harfe indirgenip birleştirilir. Türkçe'de "İ" harfinin küçüğü "i",
    /// "I" harfinin küçüğü "ı" olduğu için SQLite'ın ASCII tabanlı LIKE'ı tek başına
    /// doğru sonuç vermez; normalleştirme yazma anında yapılır.
    /// </summary>
    public string SearchText { get; set; } = string.Empty;

    // ------------------------------------------------------------------
    // Durum ve görünürlük
    // ------------------------------------------------------------------

    public Availability Availability { get; set; } = Availability.Busy;

    public EventVisibility Visibility { get; set; } = EventVisibility.Default;

    public EventStatus Status { get; set; } = EventStatus.Confirmed;

    /// <summary>
    /// Toplantı iptal edildiyse gerekçesi. İptal edilen toplantı silinmez:
    /// katılımcıların takviminde üstü çizili olarak durur ve neden iptal
    /// edildiğini görürler. Silmek bu bilgiyi yok ederdi.
    /// </summary>
    public string? CancellationReason { get; set; }

    /// <summary>False ise davet başkalarına iletilemez.</summary>
    public bool IsForwardable { get; set; } = true;

    public Guid? OrganizerUserId { get; set; }
    public User? Organizer { get; set; }

    // ------------------------------------------------------------------
    // Tekrarlama
    // ------------------------------------------------------------------

    /// <summary>
    /// RFC 5545 RRULE metni, ör. "FREQ=MONTHLY;BYDAY=3TU". Yalnızca seri kökünde dolu olur.
    /// </summary>
    public string? RecurrenceRule { get; set; }

    /// <summary>Seriden çıkarılan tarihler (EXDATE), ISO biçiminde virgülle ayrılmış.</summary>
    public string? ExDates { get; set; }

    /// <summary>Seriye elle eklenen tarihler (RDATE), ISO biçiminde virgülle ayrılmış.</summary>
    public string? RDates { get; set; }

    /// <summary>
    /// Serinin son örneğinin bitişi. Süresiz seriler için null.
    /// Aralık sorgularının süresiz serileri gereksiz yere genişletmesini önler.
    /// </summary>
    public Instant? SeriesEndUtc { get; set; }

    /// <summary>
    /// Resmi tatile denk gelen örneklere ne olacağı. Yalnızca seri kökünde anlamlıdır.
    /// </summary>
    public HolidayBehavior HolidayBehavior { get; set; } = HolidayBehavior.Include;

    /// <summary>Bu satır bir istisna ise, yerini aldığı örneğin özgün başlangıç saati (RECURRENCE-ID).</summary>
    public LocalDateTime? RecurrenceId { get; set; }

    /// <summary>Bu satır bir istisna ise, ait olduğu seri kökünün kimliği.</summary>
    public Guid? SeriesId { get; set; }
    public Event? Series { get; set; }

    /// <summary>Seri kökünün istisna satırları.</summary>
    public ICollection<Event> Exceptions { get; set; } = [];

    // ------------------------------------------------------------------
    // Senkronizasyon meta verisi (CalDAV, ICS ve dış senkron için zorunlu)
    // ------------------------------------------------------------------

    /// <summary>
    /// iCalendar SEQUENCE. Katılımcıları etkileyen her değişiklikte artar;
    /// alıcı istemciler eski güncellemeleri bununla eler.
    /// </summary>
    public int Sequence { get; set; }

    /// <summary>CalDAV ETag. Her yazmada yenilenir, koşullu istekler bunu kullanır.</summary>
    public required string ETag { get; set; }

    public Instant LastModifiedUtc { get; set; }

    /// <summary>Etkinlik dış bir hesaptan geldiyse kaynak kimliği (Faz 3).</summary>
    public string? SourceId { get; set; }

    /// <summary>Dış sistemdeki kimliği (Faz 3).</summary>
    public string? RemoteId { get; set; }

    // ------------------------------------------------------------------
    // Yaşam döngüsü
    // ------------------------------------------------------------------

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Çöp kutusu. Null değilse silinmiştir; 30 gün sonra kalıcı temizlenir.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    public ICollection<Reminder> Reminders { get; set; } = [];
    public ICollection<EventCategory> Categories { get; set; } = [];
    public ICollection<Attendee> Attendees { get; set; } = [];
    public ICollection<Attachment> Attachments { get; set; } = [];
}
