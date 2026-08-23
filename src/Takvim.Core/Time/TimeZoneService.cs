using NodaTime;
using NodaTime.TimeZones;

namespace Takvim.Core.Time;

/// <summary>
/// Yerel saat ile mutlak an arasındaki tüm çevrimler buradan geçer.
/// Doğrudan <c>DateTime</c> aritmetiği yapmak yasaktır: yaz saati geçişlerinde
/// var olmayan ve iki kez var olan saatler sessizce yanlış sonuç üretir.
/// </summary>
public sealed class TimeZoneService
{
    private readonly IDateTimeZoneProvider _provider;

    public TimeZoneService(IDateTimeZoneProvider? provider = null)
        => _provider = provider ?? DateTimeZoneProviders.Tzdb;

    /// <summary>Uygulamanın varsayılan zaman dilimi.</summary>
    public const string DefaultZoneId = "Europe/Istanbul";

    /// <summary>Kullanılan IANA zaman dilimi veritabanının sürümü, ör. "2025b".</summary>
    public string TzdbVersion => _provider.VersionId;

    public DateTimeZone Get(string? tzId)
        => _provider.GetZoneOrNull(tzId ?? DefaultZoneId) ?? _provider[DefaultZoneId];

    public bool IsKnown(string tzId) => _provider.GetZoneOrNull(tzId) is not null;

    public IReadOnlyList<string> AllZoneIds => _provider.Ids;

    /// <summary>
    /// Yerel duvar saatini mutlak ana çevirir.
    /// <para>
    /// İki kenar durum vardır ve ikisi de sessizce geçiştirilmez:
    /// <list type="bullet">
    /// <item><b>Boşluk</b> — saatler ileri alındığında var olmayan yerel saat
    /// (ör. Berlin'de 29.03.2026 02:30). Boşluk süresi kadar ileri kaydırılır,
    /// yani 02:30 -> 03:30 olur. Etkinliğin gün içindeki göreli yeri korunur.</item>
    /// <item><b>Belirsizlik</b> — saatler geri alındığında iki kez yaşanan yerel saat.
    /// Erken olan, yani ilk yaşanan seçilir.</item>
    /// </list>
    /// Bu davranış RFC 5545'in önerdiğiyle uyumludur ve tüm uygulamada tektir.
    /// </para>
    /// </summary>
    public Instant ToInstant(LocalDateTime local, string? tzId)
        => Get(tzId).ResolveLocal(local, Resolvers.LenientResolver).ToInstant();

    public LocalDateTime ToLocal(Instant instant, string? tzId)
        => instant.InZone(Get(tzId)).LocalDateTime;

    public ZonedDateTime ToZoned(Instant instant, string? tzId)
        => instant.InZone(Get(tzId));

    /// <summary>Yerel saatin o zaman diliminde gerçekten var olup olmadığı.</summary>
    public LocalResolution Resolve(LocalDateTime local, string? tzId)
    {
        var mapping = Get(tzId).MapLocal(local);
        return mapping.Count switch
        {
            0 => LocalResolution.Skipped,
            1 => LocalResolution.Unambiguous,
            _ => LocalResolution.Ambiguous,
        };
    }

    /// <summary>Belirtilen zaman dilimindeki UTC farkı, ör. "+03:00".</summary>
    public Offset OffsetAt(Instant instant, string? tzId) => Get(tzId).GetUtcOffset(instant);

    public LocalDate TodayIn(string? tzId, IClock? clock = null)
        => (clock ?? SystemClock.Instance).GetCurrentInstant().InZone(Get(tzId)).Date;
}

/// <summary>Bir yerel saatin zaman diliminde nasıl karşılandığı.</summary>
public enum LocalResolution
{
    /// <summary>Tek bir karşılığı var.</summary>
    Unambiguous,
    /// <summary>Saatler ileri alındığı için bu yerel saat hiç yaşanmadı.</summary>
    Skipped,
    /// <summary>Saatler geri alındığı için bu yerel saat iki kez yaşandı.</summary>
    Ambiguous,
}
