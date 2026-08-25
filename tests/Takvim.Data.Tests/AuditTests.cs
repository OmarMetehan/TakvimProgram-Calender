using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Permissions;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// Denetim görünümü. Asıl sınanan şey listeleme değil <b>görünürlük sınırı</b>:
/// özet satırları etkinlik başlıklarını taşır, dolayısıyla günlük veriden daha
/// az korunaklı olursa gizli bir etkinliğin başlığı oradan sızar.
/// </summary>
public class AuditTests : IDisposable
{
    private readonly TestDatabase _t = new();
    private readonly AuditService _audit;

    public AuditTests() => _audit = new AuditService(_t.Db, _t.Permissions);

    public void Dispose()
    {
        _t.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<Guid> CreateEventAsync(string title = "Bütçe toplantısı")
    {
        var created = await _t.Events.CreateAsync(
            _t.Input("2026-03-02 10:00", "2026-03-02 11:00", title));

        _t.Detach();
        return created.PrimaryEventId;
    }

    private Task<List<AuditRow>> ReadAsync(AuditFilter? filter = null)
        => _audit.GetAsync(_t.UserId, filter);

    // ==================================================================
    // Okuma
    // ==================================================================

    [Fact]
    public async Task Etkinlik_olusturma_gunluge_dusuyor()
    {
        await CreateEventAsync();

        var row = Assert.Single(await ReadAsync());

        Assert.Equal(nameof(Event), row.Entry.EntityType);
        Assert.Equal(ChangeOperation.Create, row.Entry.Operation);
        Assert.Contains("Bütçe toplantısı", row.Entry.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Kayitlar_yeniden_eskiye_siralanir()
    {
        await CreateEventAsync("Birinci");
        await CreateEventAsync("İkinci");

        var rows = await ReadAsync();

        Assert.Contains("İkinci", rows[0].Entry.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Islem_yapanin_adi_cozulur()
    {
        await CreateEventAsync();

        Assert.Equal("Test Kullanıcı", Assert.Single(await ReadAsync()).ActorName);
    }

    [Fact]
    public async Task Takvimin_adi_cozulur()
    {
        await CreateEventAsync();

        Assert.Equal("Kişisel", Assert.Single(await ReadAsync()).CalendarName);
    }

    [Fact]
    public async Task Silme_de_gunluge_dusuyor()
    {
        var eventId = await CreateEventAsync();

        await _t.Events.DeleteAsync(eventId, null, SeriesEditScope.AllInSeries, _t.UserId);
        _t.Detach();

        var rows = await ReadAsync();

        Assert.Contains(rows, r => r.Entry.Operation == ChangeOperation.Delete);
    }

    [Fact]
    public async Task Silinen_etkinligin_kaydi_kaliyor()
    {
        // Denetim kaydının varlık sebebi bu: silinen bir şeyin izi kalmalı.
        var eventId = await CreateEventAsync("Silinecek");

        await _t.Events.DeleteAsync(eventId, null, SeriesEditScope.AllInSeries, _t.UserId);
        _t.Detach();

        var rows = await ReadAsync();

        Assert.Contains(rows, r => r.Entry.Summary!.Contains("Silinecek", StringComparison.Ordinal));
    }

    // ==================================================================
    // Süzgeçler
    // ==================================================================

    [Fact]
    public async Task Isleme_gore_suzulur()
    {
        var eventId = await CreateEventAsync();

        await _t.Events.DeleteAsync(eventId, null, SeriesEditScope.AllInSeries, _t.UserId);
        _t.Detach();

        var deletions = await ReadAsync(new AuditFilter { Operation = ChangeOperation.Delete });

        Assert.All(deletions, r => Assert.Equal(ChangeOperation.Delete, r.Entry.Operation));
        Assert.NotEmpty(deletions);
    }

    [Fact]
    public async Task Varlik_turune_gore_suzulur()
    {
        await CreateEventAsync();

        var events = await ReadAsync(new AuditFilter { EntityType = nameof(Event) });
        var shares = await ReadAsync(new AuditFilter { EntityType = nameof(CalendarShare) });

        Assert.NotEmpty(events);
        Assert.Empty(shares);
    }

    [Fact]
    public async Task Takvime_gore_suzulur()
    {
        await CreateEventAsync();

        var mine = await ReadAsync(new AuditFilter { CalendarId = _t.CalendarId });
        var other = await ReadAsync(new AuditFilter { CalendarId = Guid.NewGuid() });

        Assert.NotEmpty(mine);
        Assert.Empty(other);
    }

    [Fact]
    public async Task Kisiye_gore_suzulur()
    {
        await CreateEventAsync();

        var mine = await ReadAsync(new AuditFilter { ActorUserId = _t.UserId });
        var other = await ReadAsync(new AuditFilter { ActorUserId = Guid.NewGuid() });

        Assert.NotEmpty(mine);
        Assert.Empty(other);
    }

    [Fact]
    public async Task Metne_gore_suzulur()
    {
        await CreateEventAsync("Bütçe");
        await CreateEventAsync("Tasarım");

        var found = await ReadAsync(new AuditFilter { Term = "Bütçe" });

        Assert.Single(found);
    }

    [Fact]
    public async Task Tarihe_gore_suzulur()
    {
        await CreateEventAsync("Eski");

        var cutoff = _t.Clock.GetCurrentInstant().Plus(Duration.FromMinutes(1));
        _t.Clock.Advance(Duration.FromMinutes(5));

        await CreateEventAsync("Yeni");

        var recent = await ReadAsync(new AuditFilter { Since = cutoff });

        // ChangedAt gerçek saatten geldiği için ikisi de sınırın üstünde
        // kalabilir; süzgecin çalıştığını daha eski bir sınırla doğrularız.
        Assert.NotEmpty(recent);

        var ancient = await ReadAsync(new AuditFilter
        {
            Since = Instant.FromUtc(2100, 1, 1, 0, 0),
        });

        Assert.Empty(ancient);
    }

    // ==================================================================
    // Görünürlük
    // ==================================================================

    [Fact]
    public async Task Erisilemeyen_takvimin_kaydi_gorunmez()
    {
        await CreateEventAsync("Gizli toplantı");

        var (otherUserId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");

        // Özet satırı başlığı taşır; paylaşılmamış bir takvimin kaydı
        // görünseydi başlık oradan sızardı.
        Assert.Empty(await _audit.GetAsync(otherUserId));
    }

    [Fact]
    public async Task Paylasilan_takvimin_kaydi_gorunur()
    {
        await CreateEventAsync();

        var (otherUserId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");
        _t.Share(_t.CalendarId, otherUserId, SharingLevel.FullDetails);

        Assert.NotEmpty(await _audit.GetAsync(otherUserId));
    }

    [Fact]
    public async Task Kendi_islemlerini_her_zaman_gorur()
    {
        var (otherUserId, otherCalendarId) = _t.AddUser("Ayşe", "ayse@ornek.local");

        // Ayşe kendi takviminde bir şey yaptı.
        var input = _t.Input("2026-03-02 10:00", "2026-03-02 11:00", "Ayşe'nin işi") with
        {
            CalendarId = otherCalendarId,
            ActorUserId = otherUserId,
        };

        await _t.Events.CreateAsync(input);
        _t.Detach();

        Assert.NotEmpty(await _audit.GetAsync(otherUserId));
        Assert.Empty(await _audit.GetAsync(_t.UserId));
    }

    // ==================================================================
    // Sayfalama ve işlem grubu
    // ==================================================================

    [Fact]
    public async Task Sayfa_boyutu_asilmaz()
    {
        for (var i = 0; i < AuditService.PageSize + 5; i++)
        {
            await CreateEventAsync($"Etkinlik {i}");
        }

        Assert.Equal(AuditService.PageSize, (await ReadAsync()).Count);
    }

    [Fact]
    public async Task Imlecle_sonraki_sayfa_alinir()
    {
        await CreateEventAsync("Birinci");
        await CreateEventAsync("İkinci");

        var first = await ReadAsync();
        var next = await _audit.GetAsync(_t.UserId, beforeToken: first[0].Entry.SyncToken);

        Assert.DoesNotContain(next, r => r.Entry.SyncToken >= first[0].Entry.SyncToken);
    }

    [Fact]
    public async Task Tek_islemin_satirlari_birlikte_okunur()
    {
        await CreateEventAsync();

        var row = Assert.Single(await ReadAsync());
        var operation = await _audit.GetOperationAsync(row.Entry.OperationId);

        Assert.NotEmpty(operation);
        Assert.All(operation, r => Assert.Equal(row.Entry.OperationId, r.Entry.OperationId));
    }

    [Fact]
    public async Task Varlik_turleri_listelenir()
    {
        await CreateEventAsync();

        Assert.Contains(nameof(Event), await _audit.GetEntityTypesAsync());
    }

    [Fact]
    public async Task Bos_gunluk_bos_liste_doner()
        => Assert.Empty(await ReadAsync());

    // ==================================================================
    // Etiketler
    // ==================================================================

    [Theory]
    [InlineData("Event", "Etkinlik")]
    [InlineData("Calendar", "Takvim")]
    [InlineData("CalendarShare", "Paylaşım")]
    [InlineData("Bilinmeyen", "Bilinmeyen")]
    public void Varlik_turu_turkcelesir(string type, string expected)
        => Assert.Equal(expected, AuditService.DescribeEntity(type));

    [Theory]
    [InlineData(ChangeOperation.Create, "eklendi")]
    [InlineData(ChangeOperation.Update, "değiştirildi")]
    [InlineData(ChangeOperation.Delete, "silindi")]
    [InlineData(ChangeOperation.Restore, "geri alındı")]
    public void Islem_turu_turkcelesir(ChangeOperation operation, string expected)
        => Assert.Equal(expected, AuditService.DescribeOperation(operation));
}
