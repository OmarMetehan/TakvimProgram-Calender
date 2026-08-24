namespace Takvim.Core.Domain;

/// <summary>
/// Bir istemcinin (iPhone, Thunderbird, macOS Takvim) CalDAV ile bağlanmak için
/// kullandığı uygulama parolası.
/// <para>
/// Kullanıcının kendi parolası yoktur — bu uygulamada oturum açma yok. Her
/// istemci için ayrı bir parola üretilir; böylece bir cihaz kaybolduğunda
/// yalnızca onun erişimi iptal edilir.
/// </para>
/// <para>
/// Parolanın kendisi saklanmaz, yalnızca tuzlanmış özeti tutulur. Üretildiği an
/// bir kez gösterilir; kaybedilirse yenisi üretilir.
/// </para>
/// </summary>
public class AppPassword
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Kullanıcının verdiği ad, ör. "iPhone" ya da "İş dizüstü".</summary>
    public required string Label { get; set; }

    /// <summary>PBKDF2 ile türetilmiş özet.</summary>
    public required byte[] Hash { get; set; }

    /// <summary>Her parola için ayrı üretilen tuz.</summary>
    public required byte[] Salt { get; set; }

    /// <summary>
    /// Parolanın ilk dört karakteri. Listede hangi parolanın hangisi olduğunu
    /// ayırt etmeye yarar; tek başına bir işe yaramaz.
    /// </summary>
    public required string Prefix { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>En son ne zaman kullanıldı. Kullanılmayan parolaları ayıklamaya yarar.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Kullanıcı iptal ettiyse dolu olur; iptal edilmiş parola kabul edilmez.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsActive => RevokedAt is null;
}
