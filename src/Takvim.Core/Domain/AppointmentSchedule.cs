using NodaTime;

namespace Takvim.Core.Domain;

/// <summary>
/// Randevu sayfası: "şu saatlerde bana randevu alabilirsiniz" tanımı.
/// <para>
/// Takvimdeki boşluklar doğrudan randevuya açılmaz. Bir kişinin takvimi boş
/// olabilir ama o saatte randevu kabul etmiyor olabilir; ikisi ayrı sorulardır.
/// Bu yüzden randevu pencereleri <b>ayrıca</b> tanımlanır ve meşguliyet bu
/// pencerelerin içinden düşülür.
/// </para>
/// </summary>
public class AppointmentSchedule
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid OwnerUserId { get; set; }
    public User? Owner { get; set; }

    /// <summary>Randevu alanın gördüğü başlık, ör. "30 dakikalık görüşme".</summary>
    public required string Name { get; set; }

    /// <summary>
    /// Adres satırında görünen kısa ad. Aynı kişinin iki sayfası olabileceği
    /// için benzersizdir.
    /// </summary>
    public required string Slug { get; set; }

    public string? Description { get; set; }

    /// <summary>Randevunun süresi, dakika.</summary>
    public int DurationMinutes { get; set; } = 30;

    /// <summary>
    /// Randevular arası zorunlu boşluk, dakika. Arka arkaya randevu almanın
    /// önüne geçer: iki görüşme arasında nefes payı kalır.
    /// </summary>
    public int BufferMinutes { get; set; } = 5;

    /// <summary>
    /// En yakın kaç saat sonrasına randevu alınabilir. "Beş dakika sonrasına"
    /// randevu alınmasını engeller.
    /// </summary>
    public int MinimumNoticeHours { get; set; } = 4;

    /// <summary>Kaç gün ilerisine kadar randevu açılır.</summary>
    public int MaximumAdvanceDays { get; set; } = 30;

    /// <summary>Bir güne alınabilecek en fazla randevu. Null ise sınırsız.</summary>
    public int? MaximumPerDay { get; set; }

    /// <summary>Randevuların yazılacağı takvim.</summary>
    public Guid CalendarId { get; set; }
    public Calendar? Calendar { get; set; }

    /// <summary>Randevunun konumu ya da çevrimiçi toplantı bağlantısı.</summary>
    public string? LocationText { get; set; }

    /// <summary>"jitsi" ise her randevuya kendi odası üretilir.</summary>
    public string? OnlineMeetingProvider { get; set; }

    /// <summary>Kapalı sayfa randevu kabul etmez ama var olan randevular durur.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<AppointmentWindow> Windows { get; set; } = [];

    // ------------------------------------------------------------------

    /// <summary>Bir randevunun takvimde kapladığı toplam yer (tampon dahil).</summary>
    public Duration BlockLength => Duration.FromMinutes(DurationMinutes + BufferMinutes);

    public Duration Length => Duration.FromMinutes(DurationMinutes);
}

/// <summary>
/// Haftalık randevu penceresi: "salı günleri 14:00-17:00 arası".
/// <para>
/// Saatler yereldir; sayfanın sahibinin zaman diliminde okunur. Randevu alan
/// başka bir dilimdeyse çevrim gösterim anında yapılır — saklanan şey duvar
/// saatidir, etkinliklerdeki kuralla aynı.
/// </para>
/// </summary>
public class AppointmentWindow
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ScheduleId { get; set; }
    public AppointmentSchedule? Schedule { get; set; }

    public IsoDayOfWeek DayOfWeek { get; set; }

    public LocalTime Start { get; set; } = new(9, 0);
    public LocalTime End { get; set; } = new(17, 0);
}

/// <summary>Alınmış bir randevu.</summary>
public class Appointment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ScheduleId { get; set; }
    public AppointmentSchedule? Schedule { get; set; }

    /// <summary>Takvime yazılan etkinlik. Randevu iptal edilirse o da silinir.</summary>
    public Guid EventId { get; set; }
    public Event? Event { get; set; }

    /// <summary>Yerel bir hesapsa kimliği; dışarıdan alınmışsa null.</summary>
    public Guid? BookedByUserId { get; set; }

    public required string GuestName { get; set; }
    public string? GuestEmail { get; set; }

    /// <summary>Randevu alırken yazılan not; "ne görüşmek istiyorsunuz".</summary>
    public string? Note { get; set; }

    public Instant StartUtc { get; set; }
    public Instant EndUtc { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>İptal edilmiş randevu saati serbest bırakır.</summary>
    public DateTimeOffset? CancelledAt { get; set; }

    public string? CancellationReason { get; set; }

    public bool IsCancelled => CancelledAt is not null;
}
