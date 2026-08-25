using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Takvim.Core.Mail;

namespace Takvim.Server.Mail;

/// <summary>Jeton uç noktasının yanıtı.</summary>
public sealed record TokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; init; }

    public bool Success => Error is null && AccessToken is not null;

    /// <summary>Kullanıcıya gösterilecek hata metni.</summary>
    public string Problem => ErrorDescription ?? Error ?? "Bilinmeyen hata";
}

/// <summary>
/// Yetkilendirme kodunu ve yenileme anahtarını jetona çevirir.
/// <para>
/// İki sağlayıcı da RFC 6749'un aynı iki isteğini kullanır; fark yalnızca
/// adres ve istemci gizinin gönderilip gönderilmediğidir.
/// </para>
/// </summary>
public sealed class MailTokenClient(HttpClient http)
{
    /// <summary>Yetkilendirme kodunu ilk jeton çiftine çevirir.</summary>
    public Task<TokenResponse> ExchangeCodeAsync(
        MailProviderProfile profile,
        ProviderCredentials credentials,
        string code,
        string redirectUri,
        string verifier,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(credentials);

        var form = new List<KeyValuePair<string, string>>
        {
            new("client_id", credentials.ClientId),
            new("code", code),
            new("redirect_uri", redirectUri),
            new("grant_type", "authorization_code"),
            new("code_verifier", verifier),
        };

        AddSecret(form, credentials);

        return PostAsync(profile.TokenUrl, form, ct);
    }

    /// <summary>
    /// Yenileme anahtarını taze bir erişim jetonuna çevirir. Erişim jetonu
    /// saklanmaz: ömrü bir saat, taramalar arası genelde daha uzun.
    /// </summary>
    public Task<TokenResponse> RefreshAsync(
        MailProviderProfile profile,
        ProviderCredentials credentials,
        string refreshToken,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(credentials);

        var form = new List<KeyValuePair<string, string>>
        {
            new("client_id", credentials.ClientId),
            new("refresh_token", refreshToken),
            new("grant_type", "refresh_token"),
        };

        // Microsoft yenilemede de kapsamı ister; Google istemez ama zararı yok.
        if (profile.Provider == Core.Domain.MailProvider.Microsoft)
        {
            form.Add(new("scope", profile.Scope));
        }

        AddSecret(form, credentials);

        return PostAsync(profile.TokenUrl, form, ct);
    }

    // ==================================================================

    private static void AddSecret(List<KeyValuePair<string, string>> form, ProviderCredentials credentials)
    {
        if (!string.IsNullOrWhiteSpace(credentials.ClientSecret))
        {
            form.Add(new("client_secret", credentials.ClientSecret));
        }
    }

    private async Task<TokenResponse> PostAsync(
        string url, List<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await http.PostAsync(url, content, ct).ConfigureAwait(false);

            // Hata durumunda da gövde okunur: sağlayıcı gerekçeyi orada yazar.
            var parsed = await response.Content
                .ReadFromJsonAsync<TokenResponse>(ct).ConfigureAwait(false);

            return parsed ?? new TokenResponse { Error = "empty_response" };
        }
        catch (HttpRequestException ex)
        {
            return new TokenResponse { Error = "network", ErrorDescription = ex.Message };
        }
        catch (System.Text.Json.JsonException ex)
        {
            return new TokenResponse { Error = "bad_response", ErrorDescription = ex.Message };
        }
    }
}
