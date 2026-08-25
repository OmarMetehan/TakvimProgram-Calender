using System.Diagnostics;
using System.Net;
using System.Text;
using Takvim.Core.Mail;

namespace Takvim.Server.Mail;

/// <summary>Yetkilendirme akışının sonucu.</summary>
/// <param name="Code">Jetona çevrilecek yetkilendirme kodu.</param>
/// <param name="RedirectUri">Jeton isteğinde birebir tekrarlanması gereken adres.</param>
/// <param name="Verifier">PKCE doğrulayıcısı.</param>
/// <param name="Error">Başarısızsa gerekçesi.</param>
public sealed record AuthorizationResult(
    string? Code, string? RedirectUri, string? Verifier, string? Error)
{
    public bool Success => Error is null && Code is not null;
}

/// <summary>
/// Tarayıcıyı açıp yetkilendirme kodunu geri yakalar.
/// <para>
/// Masaüstü uygulamalarında önerilen yol budur (RFC 8252): giriş, kullanıcının
/// kendi tarayıcısında yapılır — parola hiçbir zaman bizim penceremize
/// yazılmaz — ve sağlayıcı sonucu <c>127.0.0.1</c> üzerinde geçici olarak
/// açtığımız dinleyiciye yönlendirir.
/// </para>
/// <para>
/// Port her seferinde işletim sisteminden istenir; sabit bir port başka bir
/// programca tutulmuş olabilir. Google ve Microsoft, loopback adreslerinde
/// değişken portu bilerek kabul eder.
/// </para>
/// </summary>
public sealed partial class LoopbackAuthorizer(ILogger<LoopbackAuthorizer> logger)
{
    /// <summary>Kullanıcının tarayıcıda giriş yapması için tanınan süre.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tarayıcı açılamadı: {Url}")]
    private partial void LogBrowserFailed(string url, Exception exception);

    /// <summary>
    /// Yetkilendirmeyi baştan sona yürütür: dinleyiciyi açar, tarayıcıyı
    /// yollar, cevabı bekler.
    /// </summary>
    public async Task<AuthorizationResult> AuthorizeAsync(
        MailProviderProfile profile,
        string clientId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        var pkce = OAuthPkce.Create();
        var state = OAuthPkce.CreateState();

        using var listener = new HttpListener();

        var port = FreePort();
        var redirectUri = $"http://127.0.0.1:{port}/";

        listener.Prefixes.Add(redirectUri);

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            return new AuthorizationResult(null, null, null,
                "Yerel dinleyici açılamadı: " + ex.Message);
        }

        var authorizeUrl = BuildAuthorizeUrl(profile, clientId, redirectUri, pkce, state);

        if (!TryOpenBrowser(authorizeUrl))
        {
            listener.Stop();
            return new AuthorizationResult(null, null, null,
                "Tarayıcı açılamadı. Adresi elle açmanız gerekiyor.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        try
        {
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token).ConfigureAwait(false);
            var query = context.Request.QueryString;

            var returnedState = query["state"];
            var code = query["code"];
            var error = query["error"];

            // Sağlayıcı kullanıcıyı tarayıcıda bıraktığı için, sonucu ona
            // orada söylemek gerekir; pencereye dönmesini bekleyemeyiz.
            await RespondAsync(context, error is null && code is not null).ConfigureAwait(false);

            if (error is not null)
            {
                return new AuthorizationResult(null, null, null,
                    error == "access_denied" ? "İzin verilmedi." : "Sağlayıcı hata döndü: " + error);
            }

            // Başka bir sekmede başlatılmış bir akışın cevabı bizimkine karışmasın.
            if (returnedState != state)
            {
                return new AuthorizationResult(null, null, null, "Yanıt bu isteğe ait değil.");
            }

            if (code is null)
            {
                return new AuthorizationResult(null, null, null, "Yetkilendirme kodu gelmedi.");
            }

            return new AuthorizationResult(code, redirectUri, pkce.Verifier, null);
        }
        catch (OperationCanceledException)
        {
            return new AuthorizationResult(null, null, null,
                "Süre doldu. Tarayıcıda giriş tamamlanmadı.");
        }
        finally
        {
            listener.Stop();
        }
    }

    // ==================================================================

    private static string BuildAuthorizeUrl(
        MailProviderProfile profile, string clientId, string redirectUri, PkcePair pkce, string state)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("client_id", clientId),
            new("redirect_uri", redirectUri),
            new("response_type", "code"),
            new("scope", profile.Scope),
            new("state", state),
            new("code_challenge", pkce.Challenge),
            new("code_challenge_method", PkcePair.Method),
        };

        if (profile.Provider == Core.Domain.MailProvider.Google)
        {
            // Google yenileme anahtarını yalnızca çevrimdışı erişim istendiğinde
            // ve yalnızca ilk onayda verir; "consent" onu her seferinde ister,
            // böylece yeniden bağlanmak da çalışır.
            parameters.Add(new("access_type", "offline"));
            parameters.Add(new("prompt", "consent"));
        }

        return profile.AuthorizeUrl + "?" + OAuthPkce.BuildQuery(parameters);
    }

    /// <summary>İşletim sisteminden boş bir port ister.</summary>
    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);

        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }

    private bool TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LogBrowserFailed(url, ex);
            return false;
        }
    }

    /// <summary>Tarayıcıda gösterilecek kapanış sayfası.</summary>
    private static async Task RespondAsync(HttpListenerContext context, bool success)
    {
        var message = success
            ? "Posta kutusu bağlandı. Bu sekmeyi kapatabilirsiniz."
            : "Bağlanamadı. Takvim penceresine dönüp yeniden deneyebilirsiniz.";

        var html = $"""
            <!doctype html>
            <html lang="tr">
            <head><meta charset="utf-8"><title>Takvim</title></head>
            <body style="font-family:system-ui;text-align:center;padding:64px 24px">
              <div style="font-size:40px">{(success ? "&#10003;" : "&#10007;")}</div>
              <p style="font-size:16px">{message}</p>
            </body>
            </html>
            """;

        var bytes = Encoding.UTF8.GetBytes(html);

        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;

        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }
}
