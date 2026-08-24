using NodaTime;

namespace Takvim.Core.Domain;

/// <summary>Katılımcının toplantıdaki rolü.</summary>
public enum AttendeeRole
{
    /// <summary>Katılımı zorunlu. Zamanlama önerileri bunları önceler.</summary>
    Required = 0,

    /// <summary>Katılımı isteğe bağlı.</summary>
    Optional = 1,

    /// <summary>Oda ya da ekipman gibi kaynak (Faz 3).</summary>
    Resource = 2,
}

/// <summary>Davete verilen yanıt. iCalendar PARTSTAT alanına eşlenir.</summary>
public enum ResponseStatus
{
    /// <summary>Henüz yanıtlanmadı.</summary>
    NeedsAction = 0,
    Accepted = 1,
    Declined = 2,
    Tentative = 3,
}

/// <summary>Katılımcının toplantıya nasıl katılacağı.</summary>
public enum AttendanceMode
{
    Unspecified = 0,
    InPerson = 1,
    Online = 2,
}

/// <summary>
/// Bir etkinliğe davetli kişi.
/// <para>
/// Bu uygulama tek makinede çalıştığı için <b>paylaşımlı model</b> kullanılır:
/// davet, katılımcının takvimine ayrı bir kopya yazmaz. Tek bir etkinlik satırı
/// vardır ve katılımcılar ona bağlanır; herkes aynı kaydı görür, bir kişinin
/// yanıtı ötekilerde anında görünür. E-posta ile davet (iTIP) modeline geçilirse
/// her katılımcının kendi kopyası gerekir — o zaman bu tablo, kopyalar arasındaki
/// eşleştirmeyi taşıyacak biçimde genişletilir.
/// </para>
/// <para>
/// Tekrarlayan etkinliklerde katılımcılar seri köküne bağlanır ve yanıt tüm seriyi
/// kapsar. Tek bir örnek için ayrı yanıt vermek, o örneğin istisna satırına kendi
/// katılımcılarının yazılmasıyla olur.
/// </para>
/// </summary>
public class Attendee
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid EventId { get; set; }
    public Event? Event { get; set; }

    /// <summary>
    /// Yerel kullanıcı. Bu makinede hesabı olmayan biri davet edilirse null kalır
    /// ve yalnızca e-posta adresiyle listelenir.
    /// </summary>
    public Guid? UserId { get; set; }
    public User? User { get; set; }

    public required string Email { get; set; }

    public required string DisplayName { get; set; }

    public AttendeeRole Role { get; set; } = AttendeeRole.Required;

    public ResponseStatus Response { get; set; } = ResponseStatus.NeedsAction;

    /// <summary>Yanıtla birlikte bırakılan açıklama.</summary>
    public string? ResponseComment { get; set; }

    public DateTimeOffset? RespondedAt { get; set; }

    /// <summary>
    /// Yanıt verildiğinde toplantının başlangıç saati.
    /// <para>
    /// Toplantı sonradan taşınırsa yanıt silinmez; bu alan sayesinde "hangi
    /// saate verilmiş bir evet" olduğu bilinir ve arayüz onu eski olarak
    /// işaretler. Yanıtı silmek bilgiyi yok etmek olurdu: "Katılacak (eski
    /// saate göre)", "yanıt yok"tan daha fazlasını söyler.
    /// </para>
    /// </summary>
    public LocalDateTime? RespondedForStartLocal { get; set; }

    public AttendanceMode Mode { get; set; } = AttendanceMode.Unspecified;

    // ------------------------------------------------------------------
    // Katılımcı yetkileri
    // ------------------------------------------------------------------

    /// <summary>Etkinliği düzenleyebilir.</summary>
    public bool CanEdit { get; set; }

    /// <summary>Başkalarını davet edebilir.</summary>
    public bool CanInviteOthers { get; set; }

    /// <summary>Katılımcı listesini görebilir. Kapalıysa yalnızca organizatörü görür.</summary>
    public bool CanSeeGuestList { get; set; } = true;

    // ------------------------------------------------------------------
    // Yeni zaman önerme
    // ------------------------------------------------------------------

    /// <summary>
    /// Katılımcı reddetmek yerine alternatif bir saat önerdiyse, önerdiği başlangıç.
    /// Organizatör tek tıkla kabul edip etkinliği oraya taşıyabilir.
    /// </summary>
    public LocalDateTime? ProposedStartLocal { get; set; }

    public LocalDateTime? ProposedEndLocal { get; set; }

    public string? ProposalNote { get; set; }

    /// <summary>Bekleyen bir zaman önerisi var mı.</summary>
    public bool HasProposal => ProposedStartLocal is not null;

    /// <summary>
    /// Yanıt, toplantının şimdiki saatinden başka bir saate verilmiş mi.
    /// <para>
    /// <see cref="Event"/> yüklenmemişse false döner: bilinmeyen durumda
    /// yanlış uyarı vermektense sessiz kalmak yeğdir.
    /// </para>
    /// </summary>
    public bool IsResponseStale
        => Response != ResponseStatus.NeedsAction
        && RespondedForStartLocal is { } given
        && Event is not null
        && Event.StartLocal != given;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Bu katılımcı satırı en son hangi değişiklikte bilgilendirildi.</summary>
    public int NotifiedSequence { get; set; } = -1;
}
