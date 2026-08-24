using Takvim.Core.Domain;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// Etkinlik şablonları. İçerik JSON olarak tek sütunda durduğu için sınamalar
/// iki şeye bakar: alanların gidip geri gelmesi ve bozuk verinin uygulamayı
/// çökertmemesi.
/// </summary>
public class TemplateTests : IDisposable
{
    private readonly TestDatabase _t = new();
    private readonly TemplateService _templates;

    public TemplateTests() => _templates = new TemplateService(_t.Db, _t.Clock);

    public void Dispose()
    {
        _t.Dispose();
        GC.SuppressFinalize(this);
    }

    private static EventTemplatePayload Payload(string title = "Ekip toplantısı") => new()
    {
        Title = title,
        DescriptionHtml = "**Gündem** aşağıda",
        AgendaText = "1. Açılış",
        LocationText = "Toplantı Odası 2",
        DurationMinutes = 45,
        Color = "peacock",
        Availability = Availability.Tentative,
        Visibility = EventVisibility.Private,
        HolidayBehavior = HolidayBehavior.Skip,
        IsForwardable = false,
        OnlineMeetingProvider = "jitsi",
        RecurrenceRule = "FREQ=WEEKLY;BYDAY=MO",
        ReminderMinutes = [10, 60],
    };

    private async Task<EventTemplate> SaveAsync(string name = "Haftalık", EventTemplatePayload? payload = null)
    {
        var saved = await _templates.SaveAsync(_t.UserId, name, payload ?? Payload());
        _t.Detach();

        return saved!;
    }

    // ==================================================================
    // Kaydetme ve okuma
    // ==================================================================

    [Fact]
    public async Task Sablon_kaydedilir_ve_listelenir()
    {
        await SaveAsync();

        Assert.Equal("Haftalık", Assert.Single(await _templates.GetAsync(_t.UserId)).Name);
    }

    [Fact]
    public async Task Tum_alanlar_gidip_geri_gelir()
    {
        var saved = await SaveAsync();
        var read = (await _templates.FindAsync(saved.Id))!.Read();

        Assert.Equal("Ekip toplantısı", read.Title);
        Assert.Equal("**Gündem** aşağıda", read.DescriptionHtml);
        Assert.Equal("1. Açılış", read.AgendaText);
        Assert.Equal("Toplantı Odası 2", read.LocationText);
        Assert.Equal(45, read.DurationMinutes);
        Assert.Equal("peacock", read.Color);
        Assert.Equal(Availability.Tentative, read.Availability);
        Assert.Equal(EventVisibility.Private, read.Visibility);
        Assert.Equal(HolidayBehavior.Skip, read.HolidayBehavior);
        Assert.False(read.IsForwardable);
        Assert.Equal("jitsi", read.OnlineMeetingProvider);
        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", read.RecurrenceRule);
        Assert.Equal([10, 60], read.ReminderMinutes);
    }

    [Fact]
    public async Task Ayni_ad_ikinci_kez_verilince_uzerine_yazilir()
    {
        await SaveAsync("Haftalık", Payload("İlk"));
        await SaveAsync("Haftalık", Payload("İkinci"));

        var single = Assert.Single(await _templates.GetAsync(_t.UserId));

        Assert.Equal("İkinci", single.Read().Title);
    }

    [Fact]
    public async Task Addaki_bosluklar_kirpilir()
    {
        var saved = await SaveAsync("  Haftalık  ");

        Assert.Equal("Haftalık", saved.Name);
    }

    [Fact]
    public async Task Bos_ad_reddedilir()
        => await Assert.ThrowsAsync<ArgumentException>(
            () => _templates.SaveAsync(_t.UserId, "   ", Payload()));

    [Fact]
    public async Task Sablon_sayisi_sinirlanir()
    {
        for (var i = 0; i < TemplateService.MaxPerUser; i++)
        {
            await SaveAsync($"Şablon {i}");
        }

        Assert.Null(await _templates.SaveAsync(_t.UserId, "Fazlalık", Payload()));
    }

    [Fact]
    public async Task Sinira_ulasinca_var_olan_yine_de_guncellenir()
    {
        for (var i = 0; i < TemplateService.MaxPerUser; i++)
        {
            await SaveAsync($"Şablon {i}");
        }

        // Güncelleme yeni satır açmaz; sınır buna takılmamalı.
        var updated = await _templates.SaveAsync(_t.UserId, "Şablon 0", Payload("Yeni içerik"));

        Assert.NotNull(updated);
        Assert.Equal("Yeni içerik", updated.Read().Title);
    }

    // ==================================================================
    // Kullanım
    // ==================================================================

    [Fact]
    public async Task Kullanilan_sablon_sayaci_artar()
    {
        var saved = await SaveAsync();

        Assert.NotNull(await _templates.UseAsync(saved.Id));
        _t.Detach();

        Assert.Equal(1, (await _templates.FindAsync(saved.Id))!.UseCount);
    }

    [Fact]
    public async Task Cok_kullanilan_sablon_ustte()
    {
        var rare = await SaveAsync("Nadir");
        var common = await SaveAsync("Sık");

        await _templates.UseAsync(common.Id);
        _t.Detach();

        var list = await _templates.GetAsync(_t.UserId);

        Assert.Equal("Sık", list[0].Name);
        Assert.Equal("Nadir", list[1].Name);
        Assert.NotEqual(rare.Id, list[0].Id);
    }

    [Fact]
    public async Task Olmayan_sablon_kullanilamaz()
        => Assert.Null(await _templates.UseAsync(Guid.NewGuid()));

    // ==================================================================
    // Yeniden adlandırma ve silme
    // ==================================================================

    [Fact]
    public async Task Sablon_yeniden_adlandirilir()
    {
        var saved = await SaveAsync();

        await _templates.RenameAsync(saved.Id, "Yeni ad");
        _t.Detach();

        Assert.Equal("Yeni ad", (await _templates.FindAsync(saved.Id))!.Name);
    }

    [Fact]
    public async Task Sablon_silinir()
    {
        var saved = await SaveAsync();

        await _templates.DeleteAsync(saved.Id);
        _t.Detach();

        Assert.Empty(await _templates.GetAsync(_t.UserId));
    }

    [Fact]
    public async Task Kullanicilarin_sablonlari_ayridir()
    {
        await SaveAsync("Benim");

        var (otherUserId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");
        await _templates.SaveAsync(otherUserId, "Benim", Payload("Ayşe'nin"));
        _t.Detach();

        // Aynı ad iki kullanıcıda ayrı ayrı durabilir.
        Assert.Equal("Ekip toplantısı", Assert.Single(await _templates.GetAsync(_t.UserId)).Read().Title);
        Assert.Equal("Ayşe'nin", Assert.Single(await _templates.GetAsync(otherUserId)).Read().Title);
    }

    // ==================================================================
    // Bozuk veri
    // ==================================================================

    [Fact]
    public async Task Bozuk_json_cokertmez()
    {
        var saved = await SaveAsync();

        var tracked = await _t.Db.EventTemplates.FindAsync(saved.Id);
        tracked!.PayloadJson = "{bu json değil";
        await _t.Db.SaveChangesAsync();
        _t.Detach();

        var read = (await _templates.FindAsync(saved.Id))!.Read();

        // Boş ama kullanılabilir bir şablon döner.
        Assert.Null(read.Title);
        Assert.Equal(60, read.DurationMinutes);
    }

    [Fact]
    public void Bos_icerik_makul_varsayilanlar_verir()
    {
        var payload = new EventTemplate { Name = "x", PayloadJson = "{}" }.Read();

        Assert.Equal(60, payload.DurationMinutes);
        Assert.Equal(Availability.Busy, payload.Availability);
        Assert.True(payload.IsForwardable);
        Assert.Empty(payload.CategoryIds);
    }
}
