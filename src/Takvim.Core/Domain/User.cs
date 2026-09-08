using NodaTime;
using Takvim.Core.Notifications;

namespace Takvim.Core.Domain;

/// <summary>
/// Kullanıcı. Faz 1'de tek bir yerel kullanıcı vardır, ancak katılımcılar, paylaşım
/// ve vekil erişimi bu tabloya dayanacağı için şema baştan çok kullanıcılı kurulur.
/// </summary>
public class User
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public required string DisplayName { get; set; }

    /// <summary>Katılımcı eşleştirmesi ve iCalendar ORGANIZER/ATTENDEE alanları için.</summary>
    public required string Email { get; set; }

    /// <summary>IANA zaman dilimi kimliği, ör. "Europe/Istanbul". Windows kimlikleri kullanılmaz.</summary>
    public string TimeZoneId { get; set; } = "Europe/Istanbul";

    public string Locale { get; set; } = "tr-TR";

    /// <summary>Haftanın ilk günü. Türkiye varsayılanı pazartesi.</summary>
    public DayOfWeek FirstDayOfWeek { get; set; } = DayOfWeek.Monday;

    // ------------------------------------------------------------------
    // Bildirim tercihleri
    //
    // Ayrı bir tabloya değil kullanıcı satırına yazılırlar: kullanıcı başına
    // tek satır olan bir tablo, kullanıcının kendisidir.
    // ------------------------------------------------------------------

    /// <summary>
    /// Hatırlatıcı bildirimleri açık mı. Kapalıyken hatırlatıcılar silinmez,
    /// yalnızca çalmaz: yeniden açıldığında etkinliklerin uyarıları yerinde durur.
    /// </summary>
    public bool RemindersEnabled { get; set; } = true;

    /// <summary>Sessiz saatler açık mı.</summary>
    public bool QuietHoursEnabled { get; set; }

    /// <summary>Sessizliğin başladığı yerel saat.</summary>
    public LocalTime QuietHoursStart { get; set; } = new(22, 0);

    /// <summary>Sessizliğin bittiği yerel saat.</summary>
    public LocalTime QuietHoursEnd { get; set; } = new(8, 0);

    /// <summary>Çalışılmayan günlerde (hafta sonu, resmi tatil, izin) bütün gün sessizlik.</summary>
    public bool QuietOnDaysOff { get; set; }

    /// <summary>Bildirim tercihlerinin sessiz saat bölümü.</summary>
    public QuietHours QuietHours
        => new(QuietHoursEnabled, QuietHoursStart, QuietHoursEnd, QuietOnDaysOff);

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
