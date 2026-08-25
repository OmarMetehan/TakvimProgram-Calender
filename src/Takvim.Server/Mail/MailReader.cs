using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NodaTime;
using Takvim.Core.Domain;

namespace Takvim.Server.Mail;

/// <summary>Okunmuş bir e-posta.</summary>
/// <param name="Id">Sağlayıcıdaki ileti kimliği.</param>
/// <param name="Subject">Konu satırı.</param>
/// <param name="From">Gönderenin adı ya da adresi.</param>
/// <param name="Body">Düz metne indirilmiş gövde.</param>
/// <param name="ReceivedAt">Alınma anı.</param>
public sealed record MailMessage(
    string Id, string Subject, string? From, string Body, Instant ReceivedAt);

/// <summary>Posta okuma sonucu; ağ hataları istisna değil gerekçe olarak döner.</summary>
public sealed record MailReadResult(IReadOnlyList<MailMessage> Messages, string? Error)
{
    public bool Success => Error is null;
}

/// <summary>
/// Gmail ve Microsoft Graph'tan ileti okur.
/// <para>
/// Yalnızca okunur ve yalnızca <b>yakın geçmiş</b> okunur: kutunun tamamını
/// taramak ne gerekli ne de saygılı. İki sağlayıcının API'leri farklı olduğu
/// için ayrı yollar var, ama ikisi de aynı <see cref="MailMessage"/> üretir.
/// </para>
/// </summary>
public sealed class MailReader(HttpClient http)
{
    /// <summary>Bir taramada okunacak en fazla ileti.</summary>
    public const int MaxMessages = 40;

    /// <summary>Gövdeden alınacak en fazla karakter; ayrıştırıcıya bu yeter.</summary>
    private const int MaxBodyLength = 4000;

    public Task<MailReadResult> ReadAsync(
        MailProvider provider, string accessToken, Instant since, CancellationToken ct = default)
        => provider == MailProvider.Microsoft
            ? ReadGraphAsync(accessToken, since, ct)
            : ReadGmailAsync(accessToken, since, ct);

    // ==================================================================
    // Gmail
    // ==================================================================

    private async Task<MailReadResult> ReadGmailAsync(
        string accessToken, Instant since, CancellationToken ct)
    {
        // Gmail sorgusu saniye çözünürlüğünde Unix zamanı kabul eder.
        var query = Uri.EscapeDataString(
            $"after:{since.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)} -in:spam -in:trash");

        var listUrl = "https://gmail.googleapis.com/gmail/v1/users/me/messages"
                    + $"?q={query}&maxResults={MaxMessages.ToString(CultureInfo.InvariantCulture)}";

        var list = await GetJsonAsync(listUrl, accessToken, ct).ConfigureAwait(false);
        if (list.Error is { } listError) return new MailReadResult([], listError);

        using var listDocument = list.Document!;

        if (!listDocument.RootElement.TryGetProperty("messages", out var ids))
        {
            return new MailReadResult([], null);
        }

        var messages = new List<MailMessage>();

        foreach (var entry in ids.EnumerateArray())
        {
            if (ct.IsCancellationRequested) break;
            if (entry.GetProperty("id").GetString() is not { } id) continue;

            var detail = await GetJsonAsync(
                $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{id}?format=full",
                accessToken, ct).ConfigureAwait(false);

            // Tek bir iletinin okunamaması taramayı durdurmaz.
            if (detail.Error is not null) continue;

            using var document = detail.Document!;

            if (ParseGmailMessage(document.RootElement, id) is { } message) messages.Add(message);
        }

        return new MailReadResult(messages, null);
    }

    private static MailMessage? ParseGmailMessage(JsonElement root, string id)
    {
        if (!root.TryGetProperty("payload", out var payload)) return null;

        var subject = HeaderValue(payload, "Subject") ?? "(konusuz)";
        var from = HeaderValue(payload, "From");

        var received = root.TryGetProperty("internalDate", out var internalDate)
                    && long.TryParse(internalDate.GetString(), CultureInfo.InvariantCulture, out var epochMs)
            ? Instant.FromUnixTimeMilliseconds(epochMs)
            : SystemClock.Instance.GetCurrentInstant();

        var body = new StringBuilder();
        CollectGmailText(payload, body);

        // Gövde okunamadıysa Gmail'in kendi özeti kullanılır; ayrıştırıcı için
        // çoğu zaman yeterlidir.
        if (body.Length == 0 && root.TryGetProperty("snippet", out var snippet))
        {
            body.Append(snippet.GetString());
        }

        return new MailMessage(id, subject, from, Trim(body.ToString()), received);
    }

    private static string? HeaderValue(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty("headers", out var headers)) return null;

        foreach (var header in headers.EnumerateArray())
        {
            if (header.TryGetProperty("name", out var headerName)
                && string.Equals(headerName.GetString(), name, StringComparison.OrdinalIgnoreCase))
            {
                return header.TryGetProperty("value", out var value) ? value.GetString() : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Çok parçalı iletiden düz metni toplar. Gmail gövdeyi ağaç olarak
    /// verir; düz metin parçası varsa o yeğlenir, yoksa HTML sadeleştirilir.
    /// </summary>
    private static void CollectGmailText(JsonElement part, StringBuilder builder)
    {
        if (builder.Length >= MaxBodyLength) return;

        var mimeType = part.TryGetProperty("mimeType", out var mime) ? mime.GetString() : null;

        if (part.TryGetProperty("body", out var body)
            && body.TryGetProperty("data", out var data)
            && data.GetString() is { Length: > 0 } encoded)
        {
            var text = DecodeBase64Url(encoded);

            if (mimeType == "text/html") text = Core.Text.TurkishText.StripHtml(text);

            if (mimeType is "text/plain" or "text/html") builder.AppendLine(text);
        }

        if (part.TryGetProperty("parts", out var parts))
        {
            foreach (var child in parts.EnumerateArray()) CollectGmailText(child, builder);
        }
    }

    private static string DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - (normalized.Length % 4)) % 4), '=');

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    // ==================================================================
    // Microsoft Graph
    // ==================================================================

    private async Task<MailReadResult> ReadGraphAsync(
        string accessToken, Instant since, CancellationToken ct)
    {
        var filter = Uri.EscapeDataString(
            $"receivedDateTime ge {since.ToDateTimeUtc().ToString("o", CultureInfo.InvariantCulture)}");

        var url = "https://graph.microsoft.com/v1.0/me/messages"
                + $"?$filter={filter}"
                + $"&$top={MaxMessages.ToString(CultureInfo.InvariantCulture)}"
                + "&$select=id,subject,from,receivedDateTime,bodyPreview,body"
                + "&$orderby=receivedDateTime desc";

        var response = await GetJsonAsync(url, accessToken, ct).ConfigureAwait(false);
        if (response.Error is { } error) return new MailReadResult([], error);

        using var document = response.Document!;

        if (!document.RootElement.TryGetProperty("value", out var items))
        {
            return new MailReadResult([], null);
        }

        var messages = new List<MailMessage>();

        foreach (var item in items.EnumerateArray())
        {
            if (item.GetProperty("id").GetString() is not { } id) continue;

            var subject = item.TryGetProperty("subject", out var s) ? s.GetString() : null;
            var from = GraphSender(item);

            var received = item.TryGetProperty("receivedDateTime", out var when)
                        && when.TryGetDateTimeOffset(out var offset)
                ? Instant.FromDateTimeOffset(offset)
                : SystemClock.Instance.GetCurrentInstant();

            messages.Add(new MailMessage(
                id,
                string.IsNullOrWhiteSpace(subject) ? "(konusuz)" : subject,
                from,
                Trim(GraphBody(item)),
                received));
        }

        return new MailReadResult(messages, null);
    }

    private static string? GraphSender(JsonElement item)
        => item.TryGetProperty("from", out var from)
        && from.TryGetProperty("emailAddress", out var address)
            ? address.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } display
                ? display
                : address.TryGetProperty("address", out var mail) ? mail.GetString() : null
            : null;

    private static string GraphBody(JsonElement item)
    {
        if (item.TryGetProperty("body", out var body)
            && body.TryGetProperty("content", out var content)
            && content.GetString() is { Length: > 0 } text)
        {
            var isHtml = body.TryGetProperty("contentType", out var type)
                      && string.Equals(type.GetString(), "html", StringComparison.OrdinalIgnoreCase);

            return isHtml ? Core.Text.TurkishText.StripHtml(text) : text;
        }

        return item.TryGetProperty("bodyPreview", out var preview) ? preview.GetString() ?? string.Empty : string.Empty;
    }

    // ==================================================================

    private async Task<(JsonDocument? Document, string? Error)> GetJsonAsync(
        string url, string accessToken, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (null, response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized => "Yetki süresi doldu; yeniden bağlanın.",
                    System.Net.HttpStatusCode.Forbidden => "Posta okuma izni verilmemiş.",
                    System.Net.HttpStatusCode.TooManyRequests => "Sağlayıcı hız sınırı uyguladı; sonra denenecek.",
                    _ => $"Sağlayıcı {(int)response.StatusCode} döndü.",
                });
            }

            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                return (await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false), null);
            }
        }
        catch (HttpRequestException ex)
        {
            return (null, "Sağlayıcıya ulaşılamadı: " + ex.Message);
        }
        catch (JsonException ex)
        {
            return (null, "Yanıt okunamadı: " + ex.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "Sağlayıcı zaman aşımına uğradı.");
        }
    }

    private static string Trim(string body)
        => body.Length <= MaxBodyLength ? body : body[..MaxBodyLength];
}
