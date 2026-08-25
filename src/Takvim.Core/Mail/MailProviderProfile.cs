using Takvim.Core.Domain;

namespace Takvim.Core.Mail;

/// <summary>
/// Bir posta sağlayıcısının OAuth ve API adresleri.
/// <para>
/// İki sağlayıcı da aynı akışı (yetkilendirme kodu + PKCE) kullanır; yalnızca
/// adresler ve izin adları değişir. Bu yüzden akış kodu tek, profil iki.
/// </para>
/// </summary>
public sealed record MailProviderProfile(
    MailProvider Provider,
    string DisplayName,
    string AuthorizeUrl,
    string TokenUrl,
    string Scope,
    string SetupUrl)
{
    public static readonly MailProviderProfile Google = new(
        MailProvider.Google,
        "Google (Gmail)",
        "https://accounts.google.com/o/oauth2/v2/auth",
        "https://oauth2.googleapis.com/token",

        // Yalnızca okuma. Posta silme ya da gönderme izni istenmez; istense
        // kullanıcı haklı olarak tereddüt ederdi.
        "https://www.googleapis.com/auth/gmail.readonly",
        "https://console.cloud.google.com/apis/credentials");

    public static readonly MailProviderProfile Microsoft = new(
        MailProvider.Microsoft,
        "Microsoft (Outlook)",

        // "common" hem kişisel hem kurumsal hesapları kabul eder.
        "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
        "https://login.microsoftonline.com/common/oauth2/v2.0/token",

        // offline_access olmadan yenileme anahtarı verilmez; her açılışta
        // yeniden giriş gerekirdi.
        "offline_access Mail.Read User.Read",
        "https://entra.microsoft.com");

    public static MailProviderProfile For(MailProvider provider)
        => provider == MailProvider.Microsoft ? Microsoft : Google;
}
