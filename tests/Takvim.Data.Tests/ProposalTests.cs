using System.Text;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// E-postadan çıkarılan etkinlik önerileri. Ağ ve OAuth sunucu katmanında
/// olduğu için burada yalnızca "okunmuş ileti geldi" varsayılır; sınananlar
/// aynı iletinin iki kez önerilmemesi ve önerinin kendiliğinden takvime
/// yazılmaması.
/// </summary>
public class ProposalTests : IDisposable
{
    private readonly TestDatabase _t = new();
    private readonly ProposalService _proposals;

    private Guid _accountId;

    public ProposalTests()
    {
        _proposals = new ProposalService(_t.Db, _t.Zones, _t.Clock);

        _accountId = _proposals
            .ConnectAsync(_t.UserId, MailProvider.Google, "ben@ornek.local", Token())
            .GetAwaiter().GetResult().Id;

        _t.Detach();
    }

    public void Dispose()
    {
        _t.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Sahte "şifrelenmiş" anahtar. Gerçek şifreleme işletim sistemine bağlı
    /// olduğu için sınamada yerine düz bayt dizisi kullanılır; servisin
    /// umurunda değildir, o yalnızca saklar.
    /// </summary>
    private static byte[] Token(string value = "yenileme-anahtari")
        => Encoding.UTF8.GetBytes(value);

    private static IncomingMessage Message(
        string id = "m1",
        string subject = "Bütçe toplantısı",
        string body = "Çarşamba 14:00'te görüşelim.",
        string? from = "ali@ornek.local",
        int daysAgo = 0)
        => new(id, subject, from, body,
               Instant.FromUtc(2026, 3, 2, 9, 0).Minus(Duration.FromDays(daysAgo)));

    private Task<int> IngestAsync(params IncomingMessage[] messages)
        => _proposals.IngestAsync(_accountId, messages, "Europe/Istanbul");

    // ==================================================================
    // Hesaplar
    // ==================================================================

    [Fact]
    public async Task Kutu_baglanir()
    {
        var account = Assert.Single(await _proposals.GetAccountsAsync(_t.UserId));

        Assert.Equal("ben@ornek.local", account.EmailAddress);
        Assert.True(account.IsActive);
    }

    [Fact]
    public async Task Ayni_kutu_ikinci_kez_baglaninca_anahtar_yenilenir()
    {
        var again = await _proposals.ConnectAsync(
            _t.UserId, MailProvider.Google, "ben@ornek.local", Token("yeni-anahtar"));

        _t.Detach();

        Assert.Equal(_accountId, again.Id);
        Assert.Single(await _proposals.GetAccountsAsync(_t.UserId));
        Assert.Equal("yeni-anahtar", Encoding.UTF8.GetString(again.ProtectedRefreshToken));
    }

    [Fact]
    public async Task Farkli_saglayici_ayri_kutudur()
    {
        await _proposals.ConnectAsync(
            _t.UserId, MailProvider.Microsoft, "ben@ornek.local", Token());

        _t.Detach();

        Assert.Equal(2, (await _proposals.GetAccountsAsync(_t.UserId)).Count);
    }

    [Fact]
    public async Task Bos_adres_reddedilir()
        => await Assert.ThrowsAsync<ArgumentException>(
            () => _proposals.ConnectAsync(_t.UserId, MailProvider.Google, "  ", Token()));

    [Fact]
    public async Task Kapali_kutu_taranacaklara_girmez()
    {
        await _proposals.SetActiveAsync(_accountId, false);
        _t.Detach();

        Assert.Empty(await _proposals.GetScannableAsync());
    }

    [Fact]
    public async Task Baglanti_kesilince_onerileri_de_gider()
    {
        await IngestAsync(Message());
        _t.Detach();

        await _proposals.DisconnectAsync(_accountId);
        _t.Detach();

        Assert.Empty(await _t.Db.EventProposals.ToListAsync());
    }

    [Fact]
    public async Task Baskasinin_kutusu_gorunmez()
    {
        var (otherUserId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");

        Assert.Empty(await _proposals.GetAccountsAsync(otherUserId));
    }

    // ==================================================================
    // Tarama sınırı
    // ==================================================================

    [Fact]
    public async Task Ilk_taramada_gecmisin_tamami_taranmaz()
    {
        var account = await _proposals.FindAccountAsync(_accountId);

        var from = _proposals.ScanFrom(account!);

        // Varsayılan 14 gün geriye.
        Assert.Equal(_t.Clock.GetCurrentInstant().Minus(Duration.FromDays(14)), from);
    }

    [Fact]
    public async Task Basarili_tarama_siniri_ilerletir()
    {
        var through = Instant.FromUtc(2026, 3, 2, 12, 0);

        await _proposals.RecordScanAsync(_accountId, through, error: null);
        _t.Detach();

        var account = (await _proposals.GetAccountsAsync(_t.UserId)).Single();

        Assert.Equal(through, account.ScannedThrough);
        Assert.Null(account.LastError);
        Assert.Equal(through, _proposals.ScanFrom(account));
    }

    [Fact]
    public async Task Basarisiz_tarama_siniri_ilerletmez()
    {
        await _proposals.RecordScanAsync(
            _accountId, Instant.FromUtc(2026, 3, 2, 12, 0), error: "Ağ yok");

        _t.Detach();

        var account = (await _proposals.GetAccountsAsync(_t.UserId)).Single();

        // Aynı aralık sonra yeniden denenmeli.
        Assert.Null(account.ScannedThrough);
        Assert.Equal("Ağ yok", account.LastError);
    }

    [Fact]
    public async Task Geriye_bakis_suresi_degistirilebilir()
    {
        await _proposals.SetLookbackAsync(_accountId, 3);
        _t.Detach();

        var account = (await _proposals.GetAccountsAsync(_t.UserId)).Single();

        Assert.Equal(3, account.LookbackDays);
    }

    [Fact]
    public async Task Cok_uzun_geriye_bakis_kirpilir()
    {
        await _proposals.SetLookbackAsync(_accountId, 5000);
        _t.Detach();

        Assert.Equal(90, (await _proposals.GetAccountsAsync(_t.UserId)).Single().LookbackDays);
    }

    // ==================================================================
    // Öneri üretimi
    // ==================================================================

    [Fact]
    public async Task Toplanti_postasindan_oneri_uretilir()
    {
        Assert.Equal(1, await IngestAsync(Message()));
        _t.Detach();

        var proposal = Assert.Single(await _proposals.GetPendingAsync(_t.UserId));

        Assert.Equal("Bütçe toplantısı", proposal.Title);
        Assert.True(proposal.RecognizedSchedule);
        Assert.Equal(new LocalDateTime(2026, 3, 4, 14, 0), proposal.StartLocal);
    }

    [Fact]
    public async Task Etkinlik_izi_olmayan_posta_oneri_uretmez()
    {
        var count = await IngestAsync(Message(
            subject: "Fatura", body: "Ekteki faturayı inceleyebilir misiniz?"));

        Assert.Equal(0, count);
        Assert.Empty(await _proposals.GetPendingAsync(_t.UserId));
    }

    [Fact]
    public async Task Ayni_ileti_ikinci_kez_onerilmez()
    {
        await IngestAsync(Message());
        _t.Detach();

        Assert.Equal(0, await IngestAsync(Message()));
        _t.Detach();

        Assert.Single(await _proposals.GetPendingAsync(_t.UserId));
    }

    [Fact]
    public async Task Reddedilen_ileti_de_ikinci_kez_onerilmez()
    {
        await IngestAsync(Message());
        _t.Detach();

        var proposal = Assert.Single(await _proposals.GetPendingAsync(_t.UserId));

        await _proposals.DismissAsync(proposal.Id);
        _t.Detach();

        // Kullanıcı aynı şeyi ikinci kez reddetmek zorunda kalmamalı.
        Assert.Equal(0, await IngestAsync(Message()));
    }

    [Fact]
    public async Task Gonderen_ve_dayanak_saklanir()
    {
        await IngestAsync(Message(body: "Merhaba.\nÇarşamba 14:00'te görüşelim.\nİyi günler."));
        _t.Detach();

        var proposal = Assert.Single(await _proposals.GetPendingAsync(_t.UserId));

        Assert.Equal("ali@ornek.local", proposal.From);
        Assert.Contains("14:00", proposal.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Konum_ve_baglanti_tasinir()
    {
        await IngestAsync(Message(body:
            "Çarşamba 14:00.\nYer: Toplantı Odası 3\nKatılım: https://meet.jit.si/odam"));

        _t.Detach();

        var proposal = Assert.Single(await _proposals.GetPendingAsync(_t.UserId));

        Assert.Equal("Toplantı Odası 3", proposal.LocationText);
        Assert.Equal("https://meet.jit.si/odam", proposal.OnlineMeetingUrl);
    }

    [Fact]
    public async Task Saatsiz_oneri_isaretlenir()
    {
        await IngestAsync(Message(
            subject: "Toplantı talebi", body: "Bir toplantı ayarlayabilir miyiz?"));

        _t.Detach();

        Assert.False(Assert.Single(await _proposals.GetPendingAsync(_t.UserId)).RecognizedSchedule);
    }

    [Fact]
    public async Task Bekleyen_oneri_sayisi_sinirlanir()
    {
        var messages = Enumerable
            .Range(0, ProposalService.MaxPendingPerAccount + 10)
            .Select(i => Message(id: $"m{i}"))
            .ToArray();

        var created = await IngestAsync(messages);

        Assert.Equal(ProposalService.MaxPendingPerAccount, created);
    }

    [Fact]
    public async Task Bos_liste_islem_yapmaz()
        => Assert.Equal(0, await IngestAsync());

    // ==================================================================
    // Onay ve red
    // ==================================================================

    [Fact]
    public async Task Oneri_kendiliginden_takvime_yazilmaz()
    {
        await IngestAsync(Message());
        _t.Detach();

        // Ayrıştırıcı yanılabilir; yanlış toplantıyı sessizce eklemek kötüdür.
        Assert.Empty(await _t.Db.Events.ToListAsync());
    }

    [Fact]
    public async Task Kabul_edilen_oneri_etkinlige_baglanir()
    {
        await IngestAsync(Message());
        _t.Detach();

        var proposal = Assert.Single(await _proposals.GetPendingAsync(_t.UserId));

        var created = await _t.Events.CreateAsync(
            _t.Input("2026-03-04 14:00", "2026-03-04 15:00", proposal.Title));

        _t.Detach();

        await _proposals.MarkAcceptedAsync(proposal.Id, created.PrimaryEventId);
        _t.Detach();

        var reloaded = await _proposals.FindAsync(proposal.Id);

        Assert.Equal(ProposalStatus.Accepted, reloaded!.Status);
        Assert.Equal(created.PrimaryEventId, reloaded.CreatedEventId);
        Assert.Empty(await _proposals.GetPendingAsync(_t.UserId));
    }

    [Fact]
    public async Task Tumu_birden_reddedilebilir()
    {
        await IngestAsync(Message("m1"), Message("m2", subject: "Görüşme"));
        _t.Detach();

        Assert.Equal(2, await _proposals.CountPendingAsync(_t.UserId));

        await _proposals.DismissAllAsync(_t.UserId);
        _t.Detach();

        Assert.Equal(0, await _proposals.CountPendingAsync(_t.UserId));
    }

    [Fact]
    public async Task Olmayan_oneri_islemleri_hata_vermez()
    {
        await _proposals.DismissAsync(Guid.NewGuid());
        await _proposals.MarkAcceptedAsync(Guid.NewGuid(), Guid.NewGuid());
        await _proposals.SetActiveAsync(Guid.NewGuid(), false);
        await _proposals.RecordScanAsync(Guid.NewGuid(), _t.Clock.GetCurrentInstant(), null);
    }

    [Fact]
    public async Task Baskasinin_onerileri_gorunmez()
    {
        await IngestAsync(Message());
        _t.Detach();

        var (otherUserId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");

        Assert.Empty(await _proposals.GetPendingAsync(otherUserId));
    }

    [Fact]
    public async Task Oneriler_yeniden_eskiye_siralanir()
    {
        await IngestAsync(
            Message("eski", subject: "Eski toplantı", daysAgo: 3),
            Message("yeni", subject: "Yeni toplantı", daysAgo: 0));

        _t.Detach();

        var pending = await _proposals.GetPendingAsync(_t.UserId);

        Assert.Equal("Yeni toplantı", pending[0].Title);
    }
}
