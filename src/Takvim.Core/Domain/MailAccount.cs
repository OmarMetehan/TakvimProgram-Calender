using NodaTime;

namespace Takvim.Core.Domain;

/// <summary>Posta sağlayıcısı.</summary>
public enum MailProvider
{
    /// <summary>Gmail / Google Workspace.</summary>
    Google = 0,

    /// <summary>Outlook.com / Microsoft 365.</summary>
    Microsoft = 1,
}

/// <summary>
/// Bağlanmış bir posta kutusu.
/// <para>
/// Yalnızca <b>okuma</b> izni istenir ve yalnızca yenileme anahtarı saklanır.
/// Anahtar diskte açık durmaz: işletim sisteminin kullanıcı anahtarıyla
/// şifrelenir, yani veritabanı dosyası başka bir makineye kopyalansa bile
/// çözülemez.
/// </para>
/// </summary>
public class MailAccount
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Kutuyu bağlayan yerel hesap.</summary>
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public MailProvider Provider { get; set; }

    /// <summary>Bağlanan kutunun adresi; kullanıcı hangi kutu olduğunu görsün diye.</summary>
    public required string EmailAddress { get; set; }

    /// <summary>Şifrelenmiş yenileme anahtarı. Hiçbir yerde çözülmüş hâliyle saklanmaz.</summary>
    public required byte[] ProtectedRefreshToken { get; set; }

    /// <summary>
    /// Kutunun en son ne zamana kadar tarandığı. Bir sonraki tarama buradan
    /// devam eder; aynı postalar ikinci kez okunmaz.
    /// </summary>
    public Instant? ScannedThrough { get; set; }

    public DateTimeOffset? LastScanAt { get; set; }

    /// <summary>Son taramanın hatası; arayüz bunu gösterir.</summary>
    public string? LastError { get; set; }

    /// <summary>Kapalı kutu taranmaz ama bağlantısı korunur.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Kaç gün geriye bakılacağı. İlk bağlanışta geçmişin tamamı taranmaz.</summary>
    public int LookbackDays { get; set; } = 14;

    public DateTimeOffset ConnectedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Bir e-postadan çıkarılan etkinlik önerisinin durumu.</summary>
public enum ProposalStatus
{
    /// <summary>Kullanıcının bakmasını bekliyor.</summary>
    Pending = 0,

    /// <summary>Takvime eklendi.</summary>
    Accepted = 1,

    /// <summary>Kullanıcı reddetti; aynı posta bir daha önerilmez.</summary>
    Dismissed = 2,
}

/// <summary>
/// Bir e-postadan çıkarılmış etkinlik önerisi.
/// <para>
/// Öneri <b>kendiliğinden takvime yazılmaz</b>. Ayrıştırıcı doğal dille
/// çalışır ve yanılabilir; yanlış bir toplantıyı sessizce takvime koymak,
/// hiç koymamaktan kötüdür. Kullanıcı bakar, düzeltir, onaylar.
/// </para>
/// </summary>
public class EventProposal
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid MailAccountId { get; set; }
    public MailAccount? Account { get; set; }

    /// <summary>Sağlayıcıdaki ileti kimliği. Aynı posta ikinci kez önerilmesin diye benzersizdir.</summary>
    public required string MessageId { get; set; }

    public required string Subject { get; set; }

    /// <summary>Gönderenin adı ya da adresi.</summary>
    public string? From { get; set; }

    /// <summary>İletinin ayrıştırılan bölümü; kullanıcı neye dayandığını görsün diye.</summary>
    public string? Snippet { get; set; }

    public Instant ReceivedAt { get; set; }

    // --- Çıkarılan alanlar ---

    public required string Title { get; set; }

    public LocalDateTime StartLocal { get; set; }
    public LocalDateTime EndLocal { get; set; }
    public bool IsAllDay { get; set; }

    public string? LocationText { get; set; }
    public string? OnlineMeetingUrl { get; set; }

    /// <summary>
    /// Ayrıştırıcının tarih/saat bulup bulmadığı. Bulamadıysa öneri yine
    /// gösterilir ama "saati siz seçin" diye işaretlenir.
    /// </summary>
    public bool RecognizedSchedule { get; set; }

    public ProposalStatus Status { get; set; } = ProposalStatus.Pending;

    /// <summary>Kabul edilirse oluşan etkinlik.</summary>
    public Guid? CreatedEventId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
