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

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
