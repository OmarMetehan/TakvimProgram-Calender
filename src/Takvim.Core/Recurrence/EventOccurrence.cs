using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Permissions;

namespace Takvim.Core.Recurrence;

/// <summary>
/// Bir etkinliğin belirli bir tarihteki somut örneği. Veritabanında satırı yoktur;
/// görünümler çizilirken tekrarlama motoru tarafından üretilir.
/// Tekil etkinlikler de tek örneklik bir dizi olarak buradan geçer, böylece
/// görünüm kodu tekrarlayan ve tekrarlamayan ayrımı yapmak zorunda kalmaz.
/// </summary>
public sealed record EventOccurrence
{
    /// <summary>
    /// Bu örneğin verisini taşıyan satır. Sapmış bir örnekte istisna satırı,
    /// diğer hallerde seri kökü veya tekil etkinliktir.
    /// </summary>
    public required Event Source { get; init; }

    public required LocalDateTime StartLocal { get; init; }

    /// <summary>Dışlayıcı bitiş. Bkz. <see cref="Event.EndLocal"/>.</summary>
    public required LocalDateTime EndLocal { get; init; }

    public required Instant StartUtc { get; init; }
    public required Instant EndUtc { get; init; }

    /// <summary>
    /// Örneğin seri içindeki kimliği: serinin bu örneği <i>özgün olarak</i> hangi saatte
    /// üretmiş olduğu. Sapmış örneklerde bile özgün saati taşır; "bu etkinliği düzenle"
    /// işlemi doğru örneği bununla bulur. Tekil etkinliklerde null.
    /// </summary>
    public LocalDateTime? RecurrenceId { get; init; }

    /// <summary>Bu örnek seriden sapmış mı (taşınmış veya alanları değiştirilmiş).</summary>
    public bool IsException { get; init; }

    /// <summary>
    /// Bakan kullanıcının bu örnekte ne görebildiği. Varsayılan tam erişimdir:
    /// izin çözümlemesi yapılmayan yollarda (kendi takvimi, dışa aktarma) örnek
    /// olduğu gibi kullanılır.
    /// <para>
    /// Kısıtlı örneklerde sorgu katmanı <see cref="Source"/> alanını zaten
    /// karartılmış bir kopyayla değiştirir; bu alan arayüzün ayrıca doğru
    /// davranabilmesi içindir.
    /// </para>
    /// </summary>
    public EventAccess Access { get; init; } =
        new(DetailLevel.FullDetails, CanEdit: true, CanInviteOnBehalf: true);

    /// <summary>Gösterilecek başlık; erişim düzeyine göre karartılmış olabilir.</summary>
    public string DisplayTitle => CalendarAccess.TitleFor(Access, Source.Title);

    /// <summary>Gösterilecek konum; başlık seviyesinin altında gizlenir.</summary>
    public string? DisplayLocation => CalendarAccess.LocationFor(Access, Source.LocationText);

    /// <summary>Açıklama, katılımcılar ve ekler gösterilebilir mi.</summary>
    public bool CanSeeDetails => Access.CanSeeDetails;

    /// <summary>Bakan kullanıcı bu örneği düzenleyebilir mi.</summary>
    public bool CanEdit => Access.CanEdit;

    /// <summary>Örnek bir seriye ait mi.</summary>
    public bool IsRecurring => RecurrenceId is not null;

    public bool IsAllDay => Source.IsAllDay;

    /// <summary>Görüntülenecek etkinliğin kimliği.</summary>
    public Guid EventId => Source.Id;

    /// <summary>Serinin kökü. Tekil etkinliklerde kendi kimliği.</summary>
    public Guid SeriesRootId => Source.SeriesId ?? Source.Id;

    /// <summary>Örneği tekilleştiren anahtar: aynı örnek iki kez çizilmemelidir.</summary>
    public string Key => RecurrenceId is null
        ? SeriesRootId.ToString("N")
        : $"{SeriesRootId:N}@{RecurrenceId.Value:uuuu-MM-ddTHH:mm:ss}";

    /// <summary>Örneğin verilen yarı açık aralığa değip değmediği.</summary>
    public bool Overlaps(Instant fromInclusive, Instant toExclusive)
        => StartUtc < toExclusive && EndUtc > fromInclusive;
}
