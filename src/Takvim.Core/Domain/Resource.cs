using NodaTime;

namespace Takvim.Core.Domain;

/// <summary>Kaynağın türü.</summary>
public enum ResourceKind
{
    /// <summary>Toplantı odası.</summary>
    Room = 0,

    /// <summary>Projeksiyon, kamera, araç gibi taşınabilir ekipman.</summary>
    Equipment = 1,
}

/// <summary>
/// Odanın taşıdığı donanım. Bayrak olarak tutulur: bir oda birden çok
/// özelliğe sahip olabilir ve "video konferanslı, 8 kişilik oda" gibi
/// aramalar tek bir sütun üzerinden yapılır.
/// </summary>
[Flags]
public enum ResourceFeatures
{
    None = 0,
    Projector = 1 << 0,
    VideoConference = 1 << 1,
    Whiteboard = 1 << 2,
    Phone = 1 << 3,
    Accessible = 1 << 4,
}

/// <summary>Rezervasyonun nasıl karşılanacağı.</summary>
public enum BookingPolicy
{
    /// <summary>Boşsa kendiliğinden kabul edilir. Çoğu oda böyledir.</summary>
    AutoAccept = 0,

    /// <summary>Odanın sorumlusu onaylayana kadar beklemede kalır.</summary>
    RequiresApproval = 1,
}

/// <summary>Bir kaynak talebinin durumu.</summary>
public enum ResourceBookingStatus
{
    /// <summary>Onay bekliyor.</summary>
    Requested = 0,

    /// <summary>Rezervasyon geçerli.</summary>
    Accepted = 1,

    /// <summary>Reddedildi ya da çakışma nedeniyle verilemedi.</summary>
    Declined = 2,
}

/// <summary>
/// Rezerve edilebilir bir oda ya da ekipman.
/// <para>
/// Her kaynağın <b>kendi takvimi</b> vardır: rezervasyon, o takvime konan bir
/// etkinliktir. Böylece çakışma denetimi, meşguliyet görünümü ve CalDAV
/// paylaşımı ayrıca yazılmaz — hepsi etkinlik altyapısından gelir. Kaynak
/// takvimi <see cref="CalendarKind.Resource"/> türündedir.
/// </para>
/// </summary>
public class Resource
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Kaynağın rezervasyonlarının tutulduğu takvim.</summary>
    public Guid CalendarId { get; set; }
    public Calendar? Calendar { get; set; }

    public required string Name { get; set; }

    public ResourceKind Kind { get; set; } = ResourceKind.Room;

    /// <summary>Kaç kişi alır. Ekipmanda anlamsızdır; null bırakılır.</summary>
    public int? Capacity { get; set; }

    /// <summary>Bina, kat, adres gibi serbest metin konum.</summary>
    public string? Location { get; set; }

    public ResourceFeatures Features { get; set; }

    public BookingPolicy Policy { get; set; } = BookingPolicy.AutoAccept;

    /// <summary>
    /// Onay isteyen kaynaklarda talebi karşılayacak kişi. Boşsa takvimin
    /// sahibi karşılar.
    /// </summary>
    public Guid? ApproverUserId { get; set; }

    /// <summary>
    /// Kapalı kaynak listelerde çıkmaz ama geçmiş rezervasyonları durur.
    /// Silmek yerine kapatmak, geçmişi bozmadan kullanımdan kaldırmayı sağlar.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<ResourceBooking> Bookings { get; set; } = [];

    // ------------------------------------------------------------------

    /// <summary>Ekranda görünen özet: "3. kat · 8 kişi · 📽 📹".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>(3);

            if (!string.IsNullOrWhiteSpace(Location)) parts.Add(Location);
            if (Capacity is { } capacity) parts.Add($"{capacity} kişi");

            var icons = FeatureIcons(Features);
            if (icons.Length > 0) parts.Add(icons);

            return string.Join(" · ", parts);
        }
    }

    public static string FeatureIcons(ResourceFeatures features)
    {
        var icons = new List<string>(5);

        if (features.HasFlag(ResourceFeatures.Projector)) icons.Add("📽");
        if (features.HasFlag(ResourceFeatures.VideoConference)) icons.Add("📹");
        if (features.HasFlag(ResourceFeatures.Whiteboard)) icons.Add("🖊");
        if (features.HasFlag(ResourceFeatures.Phone)) icons.Add("☎");
        if (features.HasFlag(ResourceFeatures.Accessible)) icons.Add("♿");

        return string.Join(" ", icons);
    }

    public static string FeatureName(ResourceFeatures feature) => feature switch
    {
        ResourceFeatures.Projector => "Projeksiyon",
        ResourceFeatures.VideoConference => "Video konferans",
        ResourceFeatures.Whiteboard => "Beyaz tahta",
        ResourceFeatures.Phone => "Telefon",
        ResourceFeatures.Accessible => "Engelli erişimi",
        _ => feature.ToString(),
    };
}

/// <summary>
/// Bir etkinliğin bir kaynağı tutması.
/// <para>
/// İki etkinliği birbirine bağlar: toplantının kendisi ve kaynağın takvimindeki
/// tutma kaydı. Toplantı taşınırsa tutma kaydı da taşınır, toplantı silinirse
/// tutma serbest kalır.
/// </para>
/// </summary>
public class ResourceBooking
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid ResourceId { get; set; }
    public Resource? Resource { get; set; }

    /// <summary>Kaynağı isteyen toplantı.</summary>
    public Guid EventId { get; set; }
    public Event? Event { get; set; }

    /// <summary>
    /// Kaynağın takvimindeki tutma kaydı. Talep reddedildiyse ya da onay
    /// bekliyorsa null olabilir: reddedilen bir talep odayı işgal etmemelidir.
    /// </summary>
    public Guid? HoldEventId { get; set; }

    public ResourceBookingStatus Status { get; set; } = ResourceBookingStatus.Requested;

    /// <summary>Reddedilme gerekçesi ya da onaylayanın notu.</summary>
    public string? ResponseNote { get; set; }

    public Guid RequestedByUserId { get; set; }

    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RespondedAt { get; set; }

    /// <summary>Tutmanın kapsadığı aralık; çakışma denetimi bunun üzerinden yapılır.</summary>
    public Instant StartUtc { get; set; }
    public Instant EndUtc { get; set; }
}
