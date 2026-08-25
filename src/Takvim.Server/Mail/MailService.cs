using System.Net.Http.Headers;
using System.Text.Json;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Mail;
using Takvim.Data.Services;

namespace Takvim.Server.Mail;

/// <summary>Kutu bağlama denemesinin sonucu.</summary>
public sealed record ConnectResult(MailAccount? Account, string? Error)
{
    public bool Success => Error is null;
}

/// <summary>Tarama sonucu.</summary>
public sealed record ScanResult(int NewProposals, string? Error)
{
    public bool Success => Error is null;
}

/// <summary>
/// Posta kutusu bağlama ve tarama akışını yürütür.
/// <para>
/// Parçaları birleştirir: tarayıcıda yetkilendirme, jeton değişimi, anahtarın
/// şifrelenerek saklanması, ileti okuma ve öneriye çevirme. Her parça ayrı
/// sınanabilir; buradaki iş sıralamadır.
/// </para>
/// </summary>
public sealed partial class MailService(
    LoopbackAuthorizer authorizer,
    MailTokenClient tokens,
    MailReader reader,
    ProposalService proposals,
    HttpClient http,
    IClock clock,
    ILogger<MailService> logger)
{
    [LoggerMessage(Level = LogLevel.Information, Message = "{Email} tarandı: {Count} yeni öneri.")]
    private partial void LogScanned(string email, int count);

    // ==================================================================
    // Bağlama
    // ==================================================================

    /// <summary>
    /// Tarayıcıyı açar, kullanıcı izin verince kutuyu bağlar.
    /// <para>
    /// Yalnızca yenileme anahtarı saklanır; erişim jetonunun ömrü bir saattir
    /// ve taramalar arası genelde daha uzun sürer, saklamanın anlamı yok.
    /// </para>
    /// </summary>
    public async Task<ConnectResult> ConnectAsync(
        Guid userId, MailProvider provider, CancellationToken ct = default)
    {
        if (!TokenProtector.IsSupported)
        {
            return new ConnectResult(null,
                "Bu işletim sisteminde anahtar güvenli saklanamıyor; posta kutusu bağlanamaz.");
        }

        var options = MailOAuthOptions.Load();
        var credentials = options.For(provider);

        if (!credentials.IsConfigured)
        {
            return new ConnectResult(null,
                $"Önce istemci kimliği tanımlanmalı. Ayar dosyası: {MailOAuthOptions.ConfigPath}");
        }

        var profile = MailProviderProfile.For(provider);

        var authorization = await authorizer.AuthorizeAsync(profile, credentials.ClientId, ct)
            .ConfigureAwait(false);

        if (!authorization.Success) return new ConnectResult(null, authorization.Error);

        var token = await tokens.ExchangeCodeAsync(
            profile, credentials,
            authorization.Code!, authorization.RedirectUri!, authorization.Verifier!, ct)
            .ConfigureAwait(false);

        if (!token.Success) return new ConnectResult(null, "Jeton alınamadı: " + token.Problem);

        if (token.RefreshToken is not { Length: > 0 } refreshToken)
        {
            // Google yenileme anahtarını yalnızca ilk onayda verir; "prompt=consent"
            // bunu her seferinde ister, yine de gelmediyse söylemek gerekir.
            return new ConnectResult(null,
                "Sağlayıcı yenileme anahtarı vermedi. İzni kaldırıp yeniden deneyin.");
        }

        var address = await ReadAddressAsync(provider, token.AccessToken!, ct).ConfigureAwait(false)
                   ?? "(bilinmeyen adres)";

        var account = await proposals.ConnectAsync(
            userId, provider, address, TokenProtector.Protect(refreshToken), ct).ConfigureAwait(false);

        return new ConnectResult(account, null);
    }

    // ==================================================================
    // Tarama
    // ==================================================================

    /// <summary>Tek bir kutuyu tarar ve öneri üretir.</summary>
    public async Task<ScanResult> ScanAsync(
        MailAccount account, string zoneId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        var options = MailOAuthOptions.Load();
        var credentials = options.For(account.Provider);

        if (!credentials.IsConfigured)
        {
            return await FailAsync(account, "İstemci kimliği tanımlı değil.", ct).ConfigureAwait(false);
        }

        if (!TokenProtector.IsSupported)
        {
            return await FailAsync(account, "Bu sistemde anahtar çözülemiyor.", ct).ConfigureAwait(false);
        }

        if (TokenProtector.Unprotect(account.ProtectedRefreshToken) is not { } refreshToken)
        {
            // Anahtar başka bir kullanıcıda ya da makinede üretilmiş olabilir.
            return await FailAsync(account, "Anahtar çözülemedi; kutuyu yeniden bağlayın.", ct)
                .ConfigureAwait(false);
        }

        var profile = MailProviderProfile.For(account.Provider);

        var token = await tokens.RefreshAsync(profile, credentials, refreshToken, ct).ConfigureAwait(false);

        if (!token.Success)
        {
            return await FailAsync(account, "Yetki yenilenemedi: " + token.Problem, ct).ConfigureAwait(false);
        }

        // Sınır taramadan önce alınır: tarama sürerken gelen postalar bir
        // sonraki tura kalmalı, atlanmamalı.
        var scannedThrough = clock.GetCurrentInstant();
        var since = proposals.ScanFrom(account);

        var read = await reader.ReadAsync(account.Provider, token.AccessToken!, since, ct)
            .ConfigureAwait(false);

        if (!read.Success)
        {
            return await FailAsync(account, read.Error!, ct).ConfigureAwait(false);
        }

        var incoming = read.Messages
            .Select(m => new IncomingMessage(m.Id, m.Subject, m.From, m.Body, m.ReceivedAt))
            .ToList();

        var created = await proposals.IngestAsync(account.Id, incoming, zoneId, ct).ConfigureAwait(false);

        await proposals.RecordScanAsync(account.Id, scannedThrough, error: null, ct).ConfigureAwait(false);

        if (created > 0) LogScanned(account.EmailAddress, created);

        return new ScanResult(created, null);
    }

    /// <summary>Taranabilir tüm kutuları tarar; birinin hatası ötekileri durdurmaz.</summary>
    public async Task<int> ScanAllAsync(string zoneId, CancellationToken ct = default)
    {
        var accounts = await proposals.GetScannableAsync(ct).ConfigureAwait(false);
        var total = 0;

        foreach (var account in accounts)
        {
            if (ct.IsCancellationRequested) break;

            var result = await ScanAsync(account, zoneId, ct).ConfigureAwait(false);
            total += result.NewProposals;
        }

        return total;
    }

    // ==================================================================

    private async Task<ScanResult> FailAsync(MailAccount account, string error, CancellationToken ct)
    {
        // Başarısız tarama sınırı ilerletmez; aynı aralık sonra yeniden denenir.
        await proposals.RecordScanAsync(account.Id, account.ScannedThrough ?? Instant.MinValue, error, ct)
            .ConfigureAwait(false);

        return new ScanResult(0, error);
    }

    /// <summary>
    /// Bağlanan kutunun adresini okur. Kullanıcı hangi kutuyu bağladığını
    /// görmeli; iki kutu bağlıysa hangisinin hangisi olduğu ancak böyle bilinir.
    /// </summary>
    private async Task<string?> ReadAddressAsync(
        MailProvider provider, string accessToken, CancellationToken ct)
    {
        var url = provider == MailProvider.Microsoft
            ? "https://graph.microsoft.com/v1.0/me?$select=mail,userPrincipalName"
            : "https://gmail.googleapis.com/gmail/v1/users/me/profile";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var document = await JsonDocument
                    .ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

                var root = document.RootElement;

                if (provider == MailProvider.Microsoft)
                {
                    return Value(root, "mail") ?? Value(root, "userPrincipalName");
                }

                return Value(root, "emailAddress");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // Adres okunamazsa bağlanmayı iptal etmeye değmez; yer tutucu kullanılır.
            return null;
        }
    }

    private static string? Value(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value.GetString() : null;
}
