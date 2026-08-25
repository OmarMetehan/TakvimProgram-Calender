using System.Net;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Ics;

namespace Takvim.Data.Services;

/// <summary>Bir beslemenin tazelenme sonucu.</summary>
/// <param name="EventCount">Takvime yazılan etkinlik sayısı.</param>
/// <param name="Error">Başarısızsa gerekçesi; başarılıysa null.</param>
public sealed record SubscriptionResult(int EventCount, string? Error)
{
    public bool Success => Error is null;
}

/// <summary>
/// Dış ICS beslemelerine abonelik.
/// <para>
/// Bir iş arkadaşının ya da bir kurumun yayımladığı <c>.ics</c> adresi salt
/// okunur bir takvim olarak eklenir. Bu, dış meşguliyet federasyonunun
/// hesapsız ve anahtarsız karşılığıdır: Exchange ya da Google'ın free-busy
/// uçlarına bağlanmak hesap, uygulama kaydı ve düzenli kimlik yenileme
/// gerektirirken, ICS yalnızca bir adres ister.
/// </para>
/// <para>
/// Tazeleme <b>yerine koyar</b>: beslemenin o anki içeriği takvimin tamamını
/// belirler. Salt okunur bir kaynakta birleştirme yapmanın anlamı yok — kaynak
/// bir etkinliği sildiyse bizde de gitmelidir.
/// </para>
/// </summary>
public sealed class SubscriptionService(
    TakvimDbContext db,
    IcsSerializer serializer,
    HttpClient http,
    IClock clock)
{
    /// <summary>İndirilecek en büyük besleme. Bir yıllık yoğun takvim bunun çok altındadır.</summary>
    public const long MaxFeedBytes = 10 * 1024 * 1024;

    /// <summary>Varsayılan tazeleme sıklığı.</summary>
    public const int DefaultRefreshMinutes = 360;

    /// <summary>Adres yönlendirmeleri izlenir ama sınırsız değil.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    // ==================================================================
    // Abonelikler
    // ==================================================================

    public Task<List<Calendar>> GetSubscriptionsAsync(Guid userId, CancellationToken ct = default)
        => db.Calendars
            .AsNoTracking()
            .Where(c => c.OwnerUserId == userId
                        && c.Kind == CalendarKind.Subscribed
                        && c.DeletedAt == null)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

    /// <summary>
    /// Beslemeyi ekler ve ilk içeriğini çeker. İlk çekim başarısız olursa
    /// takvim yine de kalır: adres geçici olarak erişilemez olabilir ve
    /// kullanıcıya "sonra dene" demek, eklediğini kaybettirmekten iyidir.
    /// </summary>
    public async Task<(Calendar Calendar, SubscriptionResult Result)> SubscribeAsync(
        Guid userId,
        string url,
        string? name = null,
        string color = "lavender",
        int refreshMinutes = DefaultRefreshMinutes,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var normalized = NormalizeUrl(url);
        if (normalized is null) throw new ArgumentException("Adres geçersiz.", nameof(url));

        var now = clock.GetCurrentInstant().ToDateTimeOffset();

        var calendar = new Calendar
        {
            OwnerUserId = userId,
            Name = string.IsNullOrWhiteSpace(name) ? SuggestName(normalized) : name.Trim(),
            Kind = CalendarKind.Subscribed,
            Color = color,

            // Abone takvim düzenlenemez: kaynağı biz değiliz, bir sonraki
            // tazelemede yaptığımız değişiklik zaten silinirdi.
            IsReadOnly = true,
            SourceUrl = normalized.ToString(),
            RefreshMinutes = Math.Clamp(refreshMinutes, 15, 7 * 24 * 60),
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Calendars.Add(calendar);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var result = await RefreshAsync(calendar.Id, ct).ConfigureAwait(false);

        return (calendar, result);
    }

    /// <summary>Adresi ya da tazeleme sıklığını değiştirir.</summary>
    public async Task UpdateAsync(
        Guid calendarId,
        string? name = null,
        string? color = null,
        int? refreshMinutes = null,
        CancellationToken ct = default)
    {
        var calendar = await db.Calendars
            .FirstOrDefaultAsync(c => c.Id == calendarId, ct).ConfigureAwait(false);

        if (calendar is null) return;

        if (!string.IsNullOrWhiteSpace(name)) calendar.Name = name.Trim();
        if (!string.IsNullOrWhiteSpace(color)) calendar.Color = color;
        if (refreshMinutes is { } minutes) calendar.RefreshMinutes = Math.Clamp(minutes, 15, 7 * 24 * 60);

        calendar.UpdatedAt = clock.GetCurrentInstant().ToDateTimeOffset();

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Aboneliği ve çektiği etkinlikleri kaldırır.</summary>
    public async Task UnsubscribeAsync(Guid calendarId, CancellationToken ct = default)
    {
        var isSubscription = await db.Calendars
            .AnyAsync(c => c.Id == calendarId && c.Kind == CalendarKind.Subscribed, ct)
            .ConfigureAwait(false);

        // Yanlışlıkla kişisel bir takvimi silmeyelim; bu yol yalnızca abonelikler içindir.
        if (!isSubscription) return;

        await db.Calendars.Where(c => c.Id == calendarId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    // ==================================================================
    // Tazeleme
    // ==================================================================

    /// <summary>Tek bir aboneliği yeniden çeker.</summary>
    public async Task<SubscriptionResult> RefreshAsync(Guid calendarId, CancellationToken ct = default)
    {
        var calendar = await db.Calendars
            .FirstOrDefaultAsync(c => c.Id == calendarId && c.Kind == CalendarKind.Subscribed, ct)
            .ConfigureAwait(false);

        if (calendar?.SourceUrl is not { } source) return new SubscriptionResult(0, "Abonelik bulunamadı.");

        var download = await DownloadAsync(source, ct).ConfigureAwait(false);
        if (download.Error is { } error) return new SubscriptionResult(0, error);

        var parsed = serializer.Import(download.Content!, calendarId, calendar.TimeZoneId);

        if (parsed.Events.Count == 0 && parsed.Warnings.Count > 0)
        {
            // Adres bir takvim döndürmedi; var olan içeriği silmeyiz.
            return new SubscriptionResult(0, "Adres bir takvim beslemesi değil.");
        }

        // Yerine koyma: kaynak neyi gösteriyorsa takvim odur.
        await db.Events.Where(e => e.CalendarId == calendarId).ExecuteDeleteAsync(ct).ConfigureAwait(false);

        foreach (var ev in parsed.Events)
        {
            // Dış kaynaktan gelen etkinliğin sahibi yoktur; kimse davet edemez,
            // kimse düzenleyemez.
            ev.OrganizerUserId = null;
            ev.Availability = Availability.Busy;
        }

        db.Events.AddRange(parsed.Events);

        calendar.LastSyncedAt = clock.GetCurrentInstant().ToDateTimeOffset();
        calendar.SyncToken++;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new SubscriptionResult(parsed.Events.Count, null);
    }

    /// <summary>
    /// Zamanı gelen abonelikleri tazeler. Arka plan görevi bunu çağırır.
    /// Bir beslemenin başarısızlığı diğerlerini durdurmaz.
    /// </summary>
    public async Task<int> RefreshDueAsync(CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();

        var candidates = await db.Calendars
            .AsNoTracking()
            .Where(c => c.Kind == CalendarKind.Subscribed && c.DeletedAt == null)
            .Select(c => new { c.Id, c.LastSyncedAt, c.RefreshMinutes })
            .ToListAsync(ct).ConfigureAwait(false);

        var refreshed = 0;

        foreach (var candidate in candidates)
        {
            var due = candidate.LastSyncedAt is not { } last
                   || Instant.FromDateTimeOffset(last).Plus(Duration.FromMinutes(candidate.RefreshMinutes)) <= now;

            if (!due) continue;

            var result = await RefreshAsync(candidate.Id, ct).ConfigureAwait(false);
            if (result.Success) refreshed++;
        }

        return refreshed;
    }

    // ==================================================================

    /// <summary>Beslemeyi indirir. Ağ hataları istisna değil, gerekçe olarak döner.</summary>
    private async Task<(string? Content, string? Error)> DownloadAsync(string url, CancellationToken ct)
    {
        if (NormalizeUrl(url) is not { } uri) return (null, "Adres geçersiz.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            using var response = await http
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (null, response.StatusCode switch
                {
                    HttpStatusCode.NotFound => "Adres bulunamadı (404).",
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                        => "Adres kimlik doğrulama istiyor; herkese açık bir bağlantı gerekir.",
                    _ => $"Sunucu {(int)response.StatusCode} döndü.",
                });
            }

            // Bildirilen uzunluğa güvenilmez; okunan bayt sayılır.
            if (response.Content.Headers.ContentLength is > MaxFeedBytes)
            {
                return (null, "Besleme çok büyük.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var limited = new MemoryStream();

            var buffer = new byte[81920];
            long total = 0;

            while (true)
            {
                var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (read == 0) break;

                total += read;
                if (total > MaxFeedBytes) return (null, "Besleme çok büyük.");

                limited.Write(buffer, 0, read);
            }

            return (System.Text.Encoding.UTF8.GetString(limited.ToArray()), null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "Adres zaman aşımına uğradı.");
        }
        catch (HttpRequestException ex)
        {
            return (null, "Adrese ulaşılamadı: " + ex.Message);
        }
    }

    /// <summary>
    /// Adresi denetler ve düzeltir. Takvim bağlantıları sık sık <c>webcal://</c>
    /// şemasıyla paylaşılır; o da https'tir.
    /// </summary>
    public static Uri? NormalizeUrl(string url)
    {
        var trimmed = url.Trim();

        if (trimmed.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "https://" + trimmed["webcal://".Length..];
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return null;

        // Yalnızca ağ adresleri: dosya ve diğer şemalar kabul edilmez.
        return uri.Scheme is "http" or "https" ? uri : null;
    }

    /// <summary>Ad verilmemişse adresten okunur bir ad türetir.</summary>
    private static string SuggestName(Uri uri)
    {
        var file = Path.GetFileNameWithoutExtension(uri.AbsolutePath);

        return string.IsNullOrWhiteSpace(file) || file.Length < 3
            ? uri.Host
            : $"{file} ({uri.Host})";
    }
}
