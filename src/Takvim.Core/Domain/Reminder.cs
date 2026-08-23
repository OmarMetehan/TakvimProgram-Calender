namespace Takvim.Core.Domain;

/// <summary>
/// Bir etkinliğe bağlı hatırlatıcı. Bir etkinliğin birden çok hatırlatıcısı olabilir.
/// iCalendar VALARM bileşenine eşlenir.
/// </summary>
public class Reminder
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid EventId { get; set; }
    public Event? Event { get; set; }

    /// <summary>
    /// Etkinlik başlangıcından kaç dakika önce tetikleneceği.
    /// Dakika/saat/gün/hafta seçenekleri arayüzde bu tek alana çevrilir.
    /// Negatif değer başlangıçtan sonrasını ifade eder.
    /// </summary>
    public int MinutesBefore { get; set; } = 10;

    public ReminderChannel Channel { get; set; } = ReminderChannel.InApp;

    /// <summary>Kullanıcı ertelediyse, yeniden tetikleneceği an.</summary>
    public DateTimeOffset? SnoozedUntil { get; set; }

    /// <summary>Tekrarlayan etkinliklerde hangi örnek için tetiklendiğini izler.</summary>
    public DateTimeOffset? LastFiredForOccurrenceAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
