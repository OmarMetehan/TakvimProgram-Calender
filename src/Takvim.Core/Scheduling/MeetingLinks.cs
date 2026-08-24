using System.Security.Cryptography;

namespace Takvim.Core.Scheduling;

/// <summary>
/// Toplantı ve harita bağlantıları.
/// <para>
/// Buradaki hiçbir işlem ağa çıkmaz ve hiçbir hesap gerektirmez: adresler
/// bilinen kalıplardan üretilir, açmak kullanıcının tarayıcısına kalır.
/// Teams/Zoom/Meet bağlantısı üretmek o servislerin hesabını ve API anahtarını
/// gerektirdiği için, tek tıkla üretilebilen tek seçenek hesapsız çalışan
/// Jitsi'dir; diğerleri elle yapıştırılır ve sağlayıcıları adresinden tanınır.
/// </para>
/// </summary>
public static class MeetingLinks
{
    /// <summary>Hesapsız toplantı odası açan servis.</summary>
    private const string JitsiHost = "https://meet.jit.si/";

    /// <summary>
    /// Oda adında karışabilecek harfler yoktur (0/O, 1/l/I): kullanıcı bu adresi
    /// telefonda okuyacak olabilir.
    /// </summary>
    private const string SlugAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    /// <summary>Tahmin edilmesi zor bir oda adresi üretir.</summary>
    /// <param name="title">Odanın adına konacak etkinlik başlığı; boşsa yalnızca rastgele ek kullanılır.</param>
    public static string CreateJitsiUrl(string? title = null)
    {
        var prefix = Slugify(title);
        var random = RandomSlug(10);

        // Yalnızca başlıktan üretilseydi "toplanti" odası herkesin ortak odası
        // olurdu; rastgele ek bu yüzden her zaman eklenir.
        return prefix.Length == 0 ? JitsiHost + random : $"{JitsiHost}{prefix}-{random}";
    }

    /// <summary>Adresten sağlayıcıyı tanır. Tanınmazsa null döner.</summary>
    public static string? DetectProvider(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)) return null;

        var host = parsed.Host.ToLowerInvariant();

        if (host.EndsWith("teams.microsoft.com", StringComparison.Ordinal)
            || host.EndsWith("teams.live.com", StringComparison.Ordinal)) return "teams";

        if (host.EndsWith("meet.google.com", StringComparison.Ordinal)) return "meet";
        if (host.EndsWith("zoom.us", StringComparison.Ordinal)) return "zoom";
        if (host.EndsWith("webex.com", StringComparison.Ordinal)) return "webex";
        if (host.EndsWith("jit.si", StringComparison.Ordinal)) return "jitsi";
        if (host.EndsWith("whereby.com", StringComparison.Ordinal)) return "whereby";

        return "diger";
    }

    /// <summary>Sağlayıcının ekranda görünen adı.</summary>
    public static string ProviderName(string? provider) => provider switch
    {
        "teams" => "Microsoft Teams",
        "meet" => "Google Meet",
        "zoom" => "Zoom",
        "webex" => "Webex",
        "jitsi" => "Jitsi Meet",
        "whereby" => "Whereby",
        "diger" => "Çevrimiçi toplantı",
        _ => "Çevrimiçi toplantı",
    };

    /// <summary>
    /// Serbest metin konumu haritada arayan adres. Koordinat da olsa adres de
    /// olsa aynı arama kutusuna gider.
    /// </summary>
    public static string? MapSearchUrl(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;

        // Zaten bir adresse (kullanıcı bağlantı yapıştırmışsa) olduğu gibi kalır.
        if (Uri.TryCreate(location.Trim(), UriKind.Absolute, out var existing)
            && existing.Scheme is "http" or "https")
        {
            return existing.ToString();
        }

        return "https://www.google.com/maps/search/?api=1&query="
               + Uri.EscapeDataString(location.Trim());
    }

    // ==================================================================

    /// <summary>Başlığı adres parçasına indirger: küçük harf, ASCII, tireli.</summary>
    private static string Slugify(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;

        var normalized = Text.TurkishText.Normalize(title);
        var builder = new System.Text.StringBuilder(normalized.Length);

        foreach (var ch in normalized)
        {
            if (char.IsAsciiLetterOrDigit(ch)) builder.Append(ch);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }

        var slug = builder.ToString().Trim('-');

        // Adres uzamasın; ayırt ediciliği zaten rastgele ek sağlar.
        return slug.Length > 24 ? slug[..24].TrimEnd('-') : slug;
    }

    private static string RandomSlug(int length)
    {
        var chars = new char[length];

        for (var i = 0; i < length; i++)
        {
            chars[i] = SlugAlphabet[RandomNumberGenerator.GetInt32(SlugAlphabet.Length)];
        }

        return new string(chars);
    }
}
