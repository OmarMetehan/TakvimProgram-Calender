using System.Security.Cryptography;
using System.Text;

namespace Takvim.Core.Mail;

/// <summary>
/// PKCE (RFC 7636) doğrulayıcı çifti.
/// </summary>
/// <param name="Verifier">İstemcide saklanan gizli dize.</param>
/// <param name="Challenge">Yetkilendirme adresine konan, doğrulayıcının özeti.</param>
public sealed record PkcePair(string Verifier, string Challenge)
{
    /// <summary>Google ve Microsoft'un kabul ettiği tek yöntem.</summary>
    public const string Method = "S256";
}

/// <summary>
/// Masaüstü uygulamaları için OAuth yardımcıları.
/// <para>
/// Masaüstü uygulamasında istemci gizi (client secret) gerçek bir sır
/// olamaz — program kullanıcının diskindedir, sökülebilir. OAuth bunu kabul
/// eder ve "installed app" akışında güvenliği <b>PKCE</b> sağlar: yetkilendirme
/// kodunu ele geçiren biri, doğrulayıcıyı bilmediği için jetona çeviremez.
/// </para>
/// </summary>
public static class OAuthPkce
{
    /// <summary>Doğrulayıcı uzunluğu; RFC 43-128 karakter arası ister.</summary>
    private const int VerifierBytes = 64;

    /// <summary>Yeni bir doğrulayıcı ve özeti üretir.</summary>
    public static PkcePair Create()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(VerifierBytes));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        return new PkcePair(verifier, challenge);
    }

    /// <summary>
    /// Yetkilendirme isteğine konan, geri dönüşte doğrulanan rastgele değer.
    /// Başka bir sekmede başlatılmış bir akışın cevabının bizimkine
    /// karışmasını engeller.
    /// </summary>
    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Base64'ün adres güvenli hâli: dolgu atılır, <c>+</c> ve <c>/</c>
    /// adres bileşenlerinde sorun çıkardığı için değiştirilir.
    /// </summary>
    public static string Base64Url(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>Sorgu dizesi kurar; değerler kaçışlanır.</summary>
    public static string BuildQuery(IEnumerable<KeyValuePair<string, string>> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        return string.Join('&', parameters.Select(p =>
            $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
    }
}
