using NodaTime;
using Takvim.Core.Domain;

namespace Takvim.Data.Services;

/// <summary>Tekrarlayan bir etkinlikte düzenlemenin nereye uygulanacağı.</summary>
public enum SeriesEditScope
{
    /// <summary>Yalnızca bu örnek. Seriden sapan bir istisna satırı oluşur.</summary>
    ThisOnly,

    /// <summary>
    /// Bu örnek ve sonrakiler. Özgün seri bu tarihten önce sonlandırılır,
    /// kalan kısım yeni bir seri olarak açılır.
    /// </summary>
    ThisAndFuture,

    /// <summary>
    /// Tüm seri. Tek tek taşınmış veya değiştirilmiş örnekler sıfırlanır;
    /// silinmiş örnekler silinmiş kalır.
    /// </summary>
    AllInSeries,
}

/// <summary>
/// Etkinlik düzenleyicisinin gönderdiği veri. Varlığın kendisi değildir:
/// yalnızca kullanıcının belirlediği alanları taşır, türetilmiş alanlar
/// (UTC önbelleği, arama metni, ETag, sıra numarası) servis tarafından hesaplanır.
/// </summary>
public sealed record EventInput
{
    public required Guid CalendarId { get; init; }

    public string Title { get; init; } = string.Empty;
    public string? DescriptionHtml { get; init; }
    public string? AgendaText { get; init; }
    public string? PrivateNotes { get; init; }
    public string? LocationText { get; init; }
    public string? OnlineMeetingUrl { get; init; }
    public string? OnlineMeetingProvider { get; init; }
    public string? Color { get; init; }

    public required LocalDateTime StartLocal { get; init; }

    /// <summary>Dışlayıcı bitiş. Tek günlük tüm gün etkinliğinde ertesi günün gece yarısı.</summary>
    public required LocalDateTime EndLocal { get; init; }

    /// <summary>Tüm gün etkinliklerinde null bırakılır.</summary>
    public string? StartTimeZoneId { get; init; }
    public string? EndTimeZoneId { get; init; }

    public bool IsAllDay { get; init; }

    public Availability Availability { get; init; } = Availability.Busy;
    public EventVisibility Visibility { get; init; } = EventVisibility.Default;
    public bool IsForwardable { get; init; } = true;

    /// <summary>RFC 5545 RRULE metni. Null ise tekrarlamaz.</summary>
    public string? RecurrenceRule { get; init; }

    /// <summary>Resmi tatile denk gelen örneklere ne olacağı.</summary>
    public HolidayBehavior HolidayBehavior { get; init; } = HolidayBehavior.Include;

    public IReadOnlyList<Guid> CategoryIds { get; init; } = [];

    public IReadOnlyList<ReminderInput> Reminders { get; init; } = [];

    /// <summary>Etkinliği oluşturan/düzenleyen kullanıcı. Denetim kaydına yazılır.</summary>
    public Guid? ActorUserId { get; init; }
}

public sealed record ReminderInput(int MinutesBefore, ReminderChannel Channel = ReminderChannel.InApp);

/// <summary>
/// Bir yazma işleminin sonucu. <see cref="UndoToken"/> ile işlem bir bütün olarak
/// geri alınabilir; "bu ve sonrakiler" gibi çok satıra dokunan işlemlerde bile.
/// </summary>
public sealed record EventWriteResult(
    Guid PrimaryEventId,
    Guid UndoToken,
    string Description);
