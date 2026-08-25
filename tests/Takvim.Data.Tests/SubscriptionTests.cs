using System.Net;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Ics;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// Ağ isteklerini karşılayan sahte işleyici. Gerçek bir sunucuya çıkmadan
/// hata, zaman aşımı ve büyük besleme durumlarını üretebilmek için.
/// </summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public string Body { get; set; } = string.Empty;
    public Exception? Throw { get; set; }

    /// <summary>Kaç istek geldiği; tazeleme sıklığı sınamalarında kullanılır.</summary>
    public int RequestCount { get; private set; }

    public Uri? LastUri { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        LastUri = request.RequestUri;

        if (Throw is { } error) throw error;

        return Task.FromResult(new HttpResponseMessage(Status)
        {
            Content = new StringContent(Body, System.Text.Encoding.UTF8, "text/calendar"),
        });
    }
}

/// <summary>
/// Dış ICS beslemelerine abonelik. Tazeleme "yerine koyma" olduğu için asıl
/// sınanan şey, kaynağın o anki içeriğinin takvimin tamamını belirlemesi.
/// </summary>
public class SubscriptionTests : IDisposable
{
    private readonly TestDatabase _t = new();
    private readonly FakeHttpHandler _handler = new();
    private readonly HttpClient _http;
    private readonly SubscriptionService _subscriptions;

    public SubscriptionTests()
    {
        _http = new HttpClient(_handler);
        _handler.Body = Feed(("Dış toplantı", "20260302T090000", "20260302T100000"));

        _subscriptions = new SubscriptionService(
            _t.Db, new IcsSerializer(_t.Zones), _http, _t.Clock);
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
        _t.Dispose();

        GC.SuppressFinalize(this);
    }

    private static readonly System.Globalization.CultureInfo Invariant =
        System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>Verilen etkinliklerden geçerli bir ICS metni kurar.</summary>
    private static string Feed(params (string Title, string Start, string End)[] events)
    {
        var builder = new System.Text.StringBuilder();

        builder.AppendLine("BEGIN:VCALENDAR");
        builder.AppendLine("VERSION:2.0");
        builder.AppendLine("PRODID:-//Sinama//TR");

        foreach (var (title, start, end) in events)
        {
            builder.AppendLine("BEGIN:VEVENT");
            builder.AppendLine(Invariant, $"UID:{title}@sinama");
            builder.AppendLine(Invariant, $"SUMMARY:{title}");
            builder.AppendLine(Invariant, $"DTSTART:{start}Z");
            builder.AppendLine(Invariant, $"DTEND:{end}Z");
            builder.AppendLine("END:VEVENT");
        }

        builder.AppendLine("END:VCALENDAR");
        return builder.ToString();
    }

    private Task<(Calendar Calendar, SubscriptionResult Result)> SubscribeAsync(
        string url = "https://ornek.local/takvim.ics")
        => _subscriptions.SubscribeAsync(_t.UserId, url);

    private Task<int> EventCountAsync(Guid calendarId)
        => _t.Db.Events.CountAsync(e => e.CalendarId == calendarId);

    // ==================================================================
    // Adres denetimi
    // ==================================================================

    [Theory]
    [InlineData("https://ornek.local/a.ics")]
    [InlineData("http://ornek.local/a.ics")]
    public void Ag_adresleri_kabul_edilir(string url)
        => Assert.NotNull(SubscriptionService.NormalizeUrl(url));

    [Fact]
    public void Webcal_adresi_httpse_cevrilir()
    {
        var uri = SubscriptionService.NormalizeUrl("webcal://ornek.local/a.ics");

        Assert.Equal("https", uri!.Scheme);
        Assert.Equal("ornek.local", uri.Host);
    }

    [Theory]
    [InlineData("file:///C:/gizli.ics")]
    [InlineData("ftp://ornek.local/a.ics")]
    [InlineData("bu bir adres değil")]
    [InlineData("/yerel/yol.ics")]
    public void Ag_disi_adresler_reddedilir(string url)
        => Assert.Null(SubscriptionService.NormalizeUrl(url));

    [Fact]
    public async Task Gecersiz_adresle_abone_olunamaz()
        => await Assert.ThrowsAsync<ArgumentException>(
            () => _subscriptions.SubscribeAsync(_t.UserId, "file:///C:/gizli.ics"));

    // ==================================================================
    // Abonelik
    // ==================================================================

    [Fact]
    public async Task Abonelik_salt_okunur_takvim_acar()
    {
        var (calendar, result) = await SubscribeAsync();
        _t.Detach();

        Assert.True(result.Success);
        Assert.Equal(CalendarKind.Subscribed, calendar.Kind);

        // Kaynağı biz değiliz; yaptığımız değişiklik bir sonraki tazelemede silinirdi.
        Assert.True(calendar.IsReadOnly);
        Assert.Equal(1, await EventCountAsync(calendar.Id));
    }

    [Fact]
    public async Task Ad_verilmezse_adresten_turetilir()
    {
        var (calendar, _) = await SubscribeAsync("https://ornek.local/tatiller.ics");

        Assert.Contains("tatiller", calendar.Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ilk_cekim_basarisiz_olsa_da_abonelik_kalir()
    {
        _handler.Status = HttpStatusCode.NotFound;

        var (calendar, result) = await SubscribeAsync();
        _t.Detach();

        // Adres geçici olarak erişilemez olabilir; eklediğini kaybettirmeyiz.
        Assert.False(result.Success);
        Assert.True(await _t.Db.Calendars.AnyAsync(c => c.Id == calendar.Id));
    }

    [Fact]
    public async Task Abonelik_listelenir()
    {
        await SubscribeAsync();
        _t.Detach();

        Assert.Single(await _subscriptions.GetSubscriptionsAsync(_t.UserId));
    }

    [Fact]
    public async Task Kisisel_takvim_abonelik_listesine_girmez()
    {
        await SubscribeAsync();
        _t.Detach();

        var subscriptions = await _subscriptions.GetSubscriptionsAsync(_t.UserId);

        Assert.DoesNotContain(subscriptions, c => c.Id == _t.CalendarId);
    }

    [Fact]
    public async Task Abonelikten_cikilinca_etkinlikleri_de_gider()
    {
        var (calendar, _) = await SubscribeAsync();
        _t.Detach();

        await _subscriptions.UnsubscribeAsync(calendar.Id);
        _t.Detach();

        Assert.False(await _t.Db.Calendars.AnyAsync(c => c.Id == calendar.Id));
        Assert.Equal(0, await EventCountAsync(calendar.Id));
    }

    [Fact]
    public async Task Kisisel_takvim_bu_yolla_silinemez()
    {
        await _subscriptions.UnsubscribeAsync(_t.CalendarId);
        _t.Detach();

        Assert.True(await _t.Db.Calendars.AnyAsync(c => c.Id == _t.CalendarId));
    }

    // ==================================================================
    // Tazeleme
    // ==================================================================

    [Fact]
    public async Task Tazeleme_kaynagin_o_anki_halini_yansitir()
    {
        var (calendar, _) = await SubscribeAsync();
        _t.Detach();

        // Kaynak bir etkinliği sildi, ikisini ekledi.
        _handler.Body = Feed(
            ("Yeni bir", "20260303T090000", "20260303T100000"),
            ("Yeni iki", "20260304T090000", "20260304T100000"));

        var result = await _subscriptions.RefreshAsync(calendar.Id);
        _t.Detach();

        Assert.True(result.Success);
        Assert.Equal(2, await EventCountAsync(calendar.Id));

        var titles = await _t.Db.Events
            .Where(e => e.CalendarId == calendar.Id)
            .Select(e => e.Title)
            .ToListAsync();

        Assert.DoesNotContain("Dış toplantı", titles);
    }

    [Fact]
    public async Task Bos_besleme_var_olan_icerigi_silmez()
    {
        var (calendar, _) = await SubscribeAsync();
        _t.Detach();

        // Adres bir takvim döndürmedi; elimizdekini atmayız.
        _handler.Body = "bu bir takvim değil";

        var result = await _subscriptions.RefreshAsync(calendar.Id);
        _t.Detach();

        Assert.False(result.Success);
        Assert.Equal(1, await EventCountAsync(calendar.Id));
    }

    [Fact]
    public async Task Sunucu_hatasi_icerigi_silmez()
    {
        var (calendar, _) = await SubscribeAsync();
        _t.Detach();

        _handler.Status = HttpStatusCode.InternalServerError;

        Assert.False((await _subscriptions.RefreshAsync(calendar.Id)).Success);
        _t.Detach();

        Assert.Equal(1, await EventCountAsync(calendar.Id));
    }

    [Fact]
    public async Task Kimlik_isteyen_adres_anlasilir_hata_verir()
    {
        var (calendar, _) = await SubscribeAsync();
        _t.Detach();

        _handler.Status = HttpStatusCode.Unauthorized;

        var result = await _subscriptions.RefreshAsync(calendar.Id);

        Assert.Contains("herkese açık", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ag_hatasi_cokertmez()
    {
        var (calendar, _) = await SubscribeAsync();
        _t.Detach();

        _handler.Throw = new HttpRequestException("ağ yok");

        var result = await _subscriptions.RefreshAsync(calendar.Id);

        Assert.False(result.Success);
        Assert.Contains("ulaşılamadı", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cekilen_etkinlik_mesgul_ve_sahipsizdir()
    {
        var (calendar, _) = await SubscribeAsync();
        _t.Detach();

        var ev = await _t.Db.Events.AsNoTracking().FirstAsync(e => e.CalendarId == calendar.Id);

        Assert.Null(ev.OrganizerUserId);
        Assert.Equal(Availability.Busy, ev.Availability);
    }

    [Fact]
    public async Task Olmayan_abonelik_tazelenemez()
        => Assert.False((await _subscriptions.RefreshAsync(Guid.NewGuid())).Success);

    // ==================================================================
    // Zamanı gelenler
    // ==================================================================

    [Fact]
    public async Task Zamani_gelmeyen_abonelik_tazelenmez()
    {
        await SubscribeAsync();
        _t.Detach();

        var before = _handler.RequestCount;

        Assert.Equal(0, await _subscriptions.RefreshDueAsync());
        Assert.Equal(before, _handler.RequestCount);
    }

    [Fact]
    public async Task Zamani_gelen_abonelik_tazelenir()
    {
        await SubscribeAsync();
        _t.Detach();

        _t.Clock.Advance(Duration.FromMinutes(SubscriptionService.DefaultRefreshMinutes + 1));

        Assert.Equal(1, await _subscriptions.RefreshDueAsync());
    }

    [Fact]
    public async Task Tazeleme_sikligi_degistirilebilir()
    {
        var (calendar, _) = await SubscribeAsync();
        _t.Detach();

        await _subscriptions.UpdateAsync(calendar.Id, refreshMinutes: 30);
        _t.Detach();

        _t.Clock.Advance(Duration.FromMinutes(31));

        Assert.Equal(1, await _subscriptions.RefreshDueAsync());
    }

    [Fact]
    public async Task Cok_kisa_siklik_alt_sinira_cekilir()
    {
        var (calendar, _) = await SubscribeAsync();
        _t.Detach();

        await _subscriptions.UpdateAsync(calendar.Id, refreshMinutes: 1);
        _t.Detach();

        var reloaded = await _t.Db.Calendars.AsNoTracking().FirstAsync(c => c.Id == calendar.Id);

        Assert.Equal(15, reloaded.RefreshMinutes);
    }

    [Fact]
    public async Task Ad_ve_renk_degistirilebilir()
    {
        var (calendar, _) = await SubscribeAsync();
        _t.Detach();

        await _subscriptions.UpdateAsync(calendar.Id, name: "Ekip takvimi", color: "basil");
        _t.Detach();

        var reloaded = await _t.Db.Calendars.AsNoTracking().FirstAsync(c => c.Id == calendar.Id);

        Assert.Equal("Ekip takvimi", reloaded.Name);
        Assert.Equal("basil", reloaded.Color);
    }

    // ==================================================================
    // Görünürlük
    // ==================================================================

    [Fact]
    public async Task Cekilen_etkinlikler_izgarada_gorunur()
    {
        await SubscribeAsync();
        _t.Detach();

        var seen = await _t.SeenByAsync(_t.UserId, "2026-03-02 00:00", "2026-03-03 00:00");

        Assert.Contains(seen, o => o.Source.Title == "Dış toplantı");
    }

    [Fact]
    public async Task Baska_kullanicinin_aboneligi_gorunmez()
    {
        await SubscribeAsync();
        _t.Detach();

        var (otherUserId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");

        Assert.Empty(await _subscriptions.GetSubscriptionsAsync(otherUserId));
        Assert.Empty(await _t.SeenByAsync(otherUserId, "2026-03-02 00:00", "2026-03-03 00:00"));
    }
}
