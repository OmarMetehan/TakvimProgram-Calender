namespace Takvim.Core.Permissions;

/// <summary>
/// Bir takvimin paylaşım seviyesi. Sıra anlamlıdır: büyük olan daha çok yetki verir.
/// </summary>
public enum SharingLevel
{
    /// <summary>Takvim paylaşılmamış.</summary>
    None = 0,

    /// <summary>Yalnızca meşgul/müsait bilgisi görünür.</summary>
    FreeBusy = 1,

    /// <summary>Başlık ve konum görünür.</summary>
    TitleLocation = 2,

    /// <summary>Tüm detaylar görünür.</summary>
    FullDetails = 3,

    /// <summary>Detayları görür ve değiştirebilir.</summary>
    CanEdit = 4,

    /// <summary>Tam yetki: kullanıcı adına davet de gönderebilir.</summary>
    FullControl = 5,
}

/// <summary>Görüntüleyenin bir etkinlikte gerçekte ne kadarını gördüğü.</summary>
public enum DetailLevel
{
    /// <summary>Etkinlik hiç görünmez.</summary>
    None = 0,

    /// <summary>Yalnızca "Meşgul" yazan bir blok; süre ve çakışma görünür.</summary>
    BusyOnly = 1,

    /// <summary>Başlık ve konum görünür; açıklama, katılımcı ve ekler görünmez.</summary>
    TitleLocation = 2,

    /// <summary>Her şey görünür.</summary>
    FullDetails = 3,
}

/// <summary>Görüntüleyenin bir takvim üzerindeki konumu.</summary>
/// <param name="ViewerUserId">Bakan kullanıcı.</param>
/// <param name="IsOwner">Takvimin sahibi mi.</param>
/// <param name="SharingLevel">Sahibi değilse hangi seviyede paylaşılmış.</param>
/// <param name="IsDelegate">Sahibin adına hareket eden vekil mi.</param>
/// <param name="DelegateCanSeePrivateItems">Vekil, özel işaretli öğeleri görebiliyor mu.</param>
/// <param name="IsAttendee">Etkinliğe davetli mi. Paylaşımdan bağımsız bir erişim yoludur.</param>
public sealed record ViewerContext(
    Guid ViewerUserId,
    bool IsOwner = false,
    SharingLevel SharingLevel = SharingLevel.None,
    bool IsDelegate = false,
    bool DelegateCanSeePrivateItems = false,
    bool IsAttendee = false);

/// <summary>Bir etkinliğin izin açısından önemli özellikleri.</summary>
/// <param name="Visibility">Etkinliğin kendi görünürlük işareti.</param>
/// <param name="HasPrivateCategory">Etkinlikte gizli işaretli en az bir kategori var mı.</param>
/// <param name="Availability">Meşguliyet durumu; müsait etkinlikler gizlendiğinde hiç gösterilmez.</param>
/// <param name="IsDeleted">Çöp kutusunda mı.</param>
public sealed record EventFacts(
    Domain.EventVisibility Visibility,
    bool HasPrivateCategory,
    Domain.Availability Availability,
    bool IsDeleted = false);

/// <summary>İzin çözümlemesinin sonucu.</summary>
/// <param name="Detail">Görüntüleyenin göreceği ayrıntı düzeyi.</param>
/// <param name="CanEdit">Etkinliği düzenleyebilir mi.</param>
/// <param name="CanInviteOnBehalf">Takvim sahibi adına davet gönderebilir mi.</param>
public readonly record struct EventAccess(DetailLevel Detail, bool CanEdit, bool CanInviteOnBehalf)
{
    /// <summary>Etkinlik ızgarada hiç çizilmez.</summary>
    public bool IsHidden => Detail == DetailLevel.None;

    /// <summary>Başlık gösterilebilir mi.</summary>
    public bool CanSeeTitle => Detail >= DetailLevel.TitleLocation;

    /// <summary>Açıklama, gündem, katılımcılar ve ekler gösterilebilir mi.</summary>
    public bool CanSeeDetails => Detail == DetailLevel.FullDetails;

    /// <summary>
    /// Organizatörün kendine aldığı özel notlar gösterilebilir mi. Detay
    /// seviyesinden bağımsızdır: davetli tüm detayı görür ama bu notu görmez.
    /// Düzenleyebilen görebilir — zaten alanı değiştirebilecek durumdadır.
    /// </summary>
    public bool CanSeePrivateNotes => CanEdit;

    public static readonly EventAccess Hidden = new(DetailLevel.None, false, false);
}

/// <summary>
/// İzin motoru. Bir etkinliği okuyan her yol buradan geçer.
/// <para>
/// Çözümleme sırası ve tüm kombinasyonların cevabı <c>docs/izin-modeli.md</c>
/// dosyasında tabloya dökülmüştür; buradaki kod o tablonun uygulanmasıdır.
/// Kural değişirse önce belge, sonra kod, sonra testler güncellenir.
/// </para>
/// </summary>
public static class CalendarAccess
{
    /// <summary>Görüntüleyenin bu etkinlikte ne görebileceğini ve yapabileceğini hesaplar.</summary>
    public static EventAccess Resolve(ViewerContext viewer, EventFacts eventFacts)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(eventFacts);

        // 0. Sahiplik her şeyi açar.
        if (viewer.IsOwner)
        {
            return new EventAccess(DetailLevel.FullDetails, CanEdit: true, CanInviteOnBehalf: true);
        }

        // Silinmiş etkinlikler yalnızca sahibine ve düzenleme yetkili vekile görünür.
        if (eventFacts.IsDeleted)
        {
            return viewer.IsDelegate && viewer.SharingLevel >= SharingLevel.CanEdit
                ? new EventAccess(DetailLevel.FullDetails, CanEdit: true, CanInviteOnBehalf: false)
                : EventAccess.Hidden;
        }

        // Katılımcılık paylaşımdan bağımsız bir erişim yoludur: davetli her zaman
        // tüm detayı görür, takvimi hiç paylaşılmamış olsa bile.
        if (viewer.IsAttendee)
        {
            return new EventAccess(DetailLevel.FullDetails, CanEdit: false, CanInviteOnBehalf: false);
        }

        // 1. Taban: takvimin paylaşım seviyesi.
        var (detail, canEdit, canInvite) = BaseAccess(viewer.SharingLevel);
        if (detail == DetailLevel.None) return EventAccess.Hidden;

        // 2. Etkinliğin kendi görünürlüğü tabanı yükseltir ya da düşürür.
        var restricted = false;

        switch (eventFacts.Visibility)
        {
            case Domain.EventVisibility.Public:
                detail = DetailLevel.FullDetails;
                break;

            case Domain.EventVisibility.Private:
                restricted = true;
                break;

            default:
                break;
        }

        // 3. Gizli kategori mutlaktır: "herkese açık" işaretini de yener.
        if (eventFacts.HasPrivateCategory) restricted = true;

        // 4. Vekil, adına hareket ettiği kullanıcının izinlerini devralır. Özel
        //    öğeleri görme yetkisi verilmişse kısıtlama kalkar.
        if (restricted && viewer.IsDelegate && viewer.DelegateCanSeePrivateItems)
        {
            restricted = false;
            detail = DetailLevel.FullDetails;
        }

        if (restricted)
        {
            // Müsait gösterilen bir özel etkinlik hiçbir bilgi taşımaz; çizilmez.
            if (eventFacts.Availability == Domain.Availability.Free) return EventAccess.Hidden;

            return new EventAccess(DetailLevel.BusyOnly, CanEdit: false, CanInviteOnBehalf: false);
        }

        return new EventAccess(detail, canEdit, canInvite);
    }

    /// <summary>Yalnızca paylaşım seviyesinden gelen taban yetki.</summary>
    private static (DetailLevel Detail, bool CanEdit, bool CanInvite) BaseAccess(SharingLevel level) => level switch
    {
        SharingLevel.FreeBusy => (DetailLevel.BusyOnly, false, false),
        SharingLevel.TitleLocation => (DetailLevel.TitleLocation, false, false),
        SharingLevel.FullDetails => (DetailLevel.FullDetails, false, false),
        SharingLevel.CanEdit => (DetailLevel.FullDetails, true, false),
        SharingLevel.FullControl => (DetailLevel.FullDetails, true, true),
        _ => (DetailLevel.None, false, false),
    };

    /// <summary>
    /// Ayrıntı düzeyine göre gösterilecek başlık. Gizlenmiş etkinlikler
    /// ızgarada yer kaplar ama içeriklerini ele vermez.
    /// </summary>
    public static string TitleFor(EventAccess access, string? actualTitle) => access.Detail switch
    {
        DetailLevel.None => string.Empty,
        DetailLevel.BusyOnly => "Meşgul",
        _ => string.IsNullOrWhiteSpace(actualTitle) ? "(başlıksız)" : actualTitle,
    };

    /// <summary>Ayrıntı düzeyine göre gösterilecek konum.</summary>
    public static string? LocationFor(EventAccess access, string? actualLocation)
        => access.CanSeeTitle ? actualLocation : null;
}
