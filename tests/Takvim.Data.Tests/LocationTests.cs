using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// Konum önerileri. Ayrı bir defter tutulmaz; kullanılan konumlar kaydedilirken
/// birikir, bu yüzden asıl sınanan şey aynı konumun iki kez birikmemesi.
/// </summary>
public class LocationTests : IDisposable
{
    private readonly TestDatabase _t = new();
    private readonly LocationService _locations;

    public LocationTests() => _locations = new LocationService(_t.Db, _t.Clock);

    public void Dispose()
    {
        _t.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task RecordAsync(string text)
    {
        await _locations.RecordAsync(_t.UserId, text);
        _t.Detach();
    }

    [Fact]
    public async Task Kullanilan_konum_hatirlanir()
    {
        await RecordAsync("Toplantı Odası 3");

        var suggestions = await _locations.SuggestAsync(_t.UserId);

        Assert.Equal("Toplantı Odası 3", Assert.Single(suggestions).Text);
    }

    [Fact]
    public async Task Ayni_konum_iki_kayit_acmaz()
    {
        await RecordAsync("Toplantı Odası");
        await RecordAsync("Toplantı Odası");

        var suggestion = Assert.Single(await _locations.SuggestAsync(_t.UserId));

        Assert.Equal(2, suggestion.UseCount);
    }

    [Fact]
    public async Task Yazim_farki_ayni_konum_sayilir()
    {
        // Türkçe küçültme ve aksan katlama arama katmanıyla aynı kuralları izler.
        await RecordAsync("TOPLANTI ODASI");
        await RecordAsync("toplanti odasi");

        var suggestion = Assert.Single(await _locations.SuggestAsync(_t.UserId));

        Assert.Equal(2, suggestion.UseCount);

        // Son yazım kalır: kullanıcı kısaltmayı düzeltmişse düzeltilmiş hâli görsün.
        Assert.Equal("toplanti odasi", suggestion.Text);
    }

    [Fact]
    public async Task Bosluk_farki_ayni_konum_sayilir()
    {
        await RecordAsync("Kat 3   Salon");
        await RecordAsync("Kat 3 Salon");

        Assert.Single(await _locations.SuggestAsync(_t.UserId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A")]
    public async Task Anlamsiz_konum_hatirlanmaz(string text)
    {
        await RecordAsync(text);

        Assert.Empty(await _locations.SuggestAsync(_t.UserId));
    }

    [Fact]
    public async Task Cok_kullanilan_konum_ustte()
    {
        await RecordAsync("Nadir Oda");
        await RecordAsync("Sık Oda");
        await RecordAsync("Sık Oda");
        await RecordAsync("Sık Oda");

        var suggestions = await _locations.SuggestAsync(_t.UserId);

        Assert.Equal("Sık Oda", suggestions[0].Text);
    }

    [Fact]
    public async Task Sabitlenen_konum_kullanim_sayisini_gecer()
    {
        await RecordAsync("Nadir Oda");
        await RecordAsync("Sık Oda");
        await RecordAsync("Sık Oda");

        var rare = (await _locations.SuggestAsync(_t.UserId)).Single(l => l.Text == "Nadir Oda");

        Assert.True(await _locations.TogglePinAsync(rare.Id));
        _t.Detach();

        Assert.Equal("Nadir Oda", (await _locations.SuggestAsync(_t.UserId))[0].Text);
    }

    [Fact]
    public async Task Sabitleme_geri_alinir()
    {
        await RecordAsync("Oda");
        var saved = Assert.Single(await _locations.SuggestAsync(_t.UserId));

        Assert.True(await _locations.TogglePinAsync(saved.Id));
        _t.Detach();
        Assert.False(await _locations.TogglePinAsync(saved.Id));
    }

    [Fact]
    public async Task Olmayan_konum_sabitlenemez()
        => Assert.False(await _locations.TogglePinAsync(Guid.NewGuid()));

    [Fact]
    public async Task Konum_unutulur()
    {
        await RecordAsync("Oda");
        var saved = Assert.Single(await _locations.SuggestAsync(_t.UserId));

        await _locations.ForgetAsync(saved.Id);
        _t.Detach();

        Assert.Empty(await _locations.SuggestAsync(_t.UserId));
    }

    [Fact]
    public async Task Kullanicilarin_konumlari_ayridir()
    {
        await RecordAsync("Benim Odam");

        var (otherUserId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");
        await _locations.RecordAsync(otherUserId, "Ayşe'nin Odası");
        _t.Detach();

        Assert.Equal("Benim Odam", Assert.Single(await _locations.SuggestAsync(_t.UserId)).Text);
        Assert.Equal("Ayşe'nin Odası", Assert.Single(await _locations.SuggestAsync(otherUserId)).Text);
    }

    [Fact]
    public async Task Ayni_konumu_iki_kullanici_ayri_tutar()
    {
        await RecordAsync("Ortak Salon");

        var (otherUserId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");
        await _locations.RecordAsync(otherUserId, "Ortak Salon");
        _t.Detach();

        // Benzersizlik kullanıcı başınadır; ikisinin de kendi sayacı olur.
        Assert.Equal(1, Assert.Single(await _locations.SuggestAsync(_t.UserId)).UseCount);
        Assert.Equal(1, Assert.Single(await _locations.SuggestAsync(otherUserId)).UseCount);
    }

    [Fact]
    public async Task Oneri_listesi_sinirlanir()
    {
        for (var i = 0; i < LocationService.SuggestionLimit + 5; i++)
        {
            await RecordAsync($"Oda {i}");
        }

        Assert.Equal(LocationService.SuggestionLimit, (await _locations.SuggestAsync(_t.UserId)).Count);
    }
}
