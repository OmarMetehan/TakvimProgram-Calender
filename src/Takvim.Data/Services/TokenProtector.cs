using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Takvim.Data.Services;

/// <summary>
/// Yenileme anahtarlarını diskte şifreler.
/// <para>
/// Windows'ta DPAPI kullanılır: şifre, oturum açmış <b>kullanıcının</b>
/// anahtarına bağlanır. Veritabanı dosyası başka bir makineye ya da başka bir
/// Windows hesabına kopyalansa bile çözülemez. Bu, uygulamanın kendi
/// ürettiği bir parolayı yine kendi yanında saklamasından güçlüdür — o parola
/// da aynı diskte olurdu.
/// </para>
/// <para>
/// Windows dışında DPAPI yoktur. O durumda anahtar <b>şifrelenmeden</b>
/// saklanmaz; <see cref="IsSupported"/> false döner ve posta kutusu bağlama
/// yolu kapanır. Sessizce açık metne düşmek, kullanıcının bilmediği bir risk
/// yaratırdı.
/// </para>
/// </summary>
public static class TokenProtector
{
    /// <summary>
    /// Şifrelemeye karıştırılan sabit. Anahtarın bu uygulamaya ait olduğunu
    /// belirler: aynı kullanıcının başka bir programı, kendi DPAPI kapsamında
    /// olsa bile bunu çözemez.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Takvim.MailToken.v1");

    /// <summary>
    /// Bu işletim sisteminde güvenli saklama var mı. Öznitelik derleyiciye bu
    /// özelliğin bir platform koruması olduğunu söyler; çağrı yerleri ayrıca
    /// <c>OperatingSystem.IsWindows()</c> yazmak zorunda kalmaz.
    /// </summary>
    [SupportedOSPlatformGuard("windows")]
    public static bool IsSupported => OperatingSystem.IsWindows();

    [SupportedOSPlatform("windows")]
    public static byte[] Protect(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);

        return ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
    }

    /// <summary>
    /// Çözer. Anahtar başka bir kullanıcıda ya da başka bir makinede
    /// üretilmişse çözülemez; bu bir hata değil, beklenen sonuçtur ve
    /// null dönerek bildirilir — çağıran yeniden bağlanma ister.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string? Unprotect(byte[] protectedValue)
    {
        ArgumentNullException.ThrowIfNull(protectedValue);

        try
        {
            var bytes = ProtectedData.Unprotect(
                protectedValue, Entropy, DataProtectionScope.CurrentUser);

            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
