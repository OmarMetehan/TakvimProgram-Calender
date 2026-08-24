using Takvim.Core.Scheduling;

namespace Takvim.Core.Tests;

/// <summary>
/// Toplantı ve harita bağlantıları. Hiçbiri ağa çıkmaz; sınanan şey adreslerin
/// doğru ve tahmin edilemez üretilmesi.
/// </summary>
public class MeetingLinkTests
{
    // ==================================================================
    // Oda adresi üretimi
    // ==================================================================

    [Fact]
    public void Uretilen_adres_gecerli_bir_uri()
    {
        var url = MeetingLinks.CreateJitsiUrl("Haftalık Toplantı");

        Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var parsed));
        Assert.Equal("https", parsed!.Scheme);
        Assert.Equal("meet.jit.si", parsed.Host);
    }

    [Fact]
    public void Ayni_baslik_farkli_oda_uretir()
    {
        // Yalnızca başlıktan üretilseydi "toplanti" odası herkesin ortak odası olurdu.
        var first = MeetingLinks.CreateJitsiUrl("Toplantı");
        var second = MeetingLinks.CreateJitsiUrl("Toplantı");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Baslik_adrese_okunur_bicimde_girer()
    {
        var url = MeetingLinks.CreateJitsiUrl("Bütçe Görüşmesi");

        Assert.Contains("butce-gorusmesi-", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Bassiz_etkinlik_de_adres_alir()
    {
        var url = MeetingLinks.CreateJitsiUrl(null);

        Assert.StartsWith("https://meet.jit.si/", url, StringComparison.Ordinal);
        Assert.True(url.Length > "https://meet.jit.si/".Length);
    }

    [Fact]
    public void Adres_karisabilecek_harf_icermez()
    {
        // Adres telefonda okunabilir olmalı: 0/O ve 1/l/I ayrımı kaybolur.
        var url = MeetingLinks.CreateJitsiUrl(null);
        var room = url["https://meet.jit.si/".Length..];

        Assert.DoesNotContain('0', room);
        Assert.DoesNotContain('1', room);
        Assert.DoesNotContain('l', room);
        Assert.DoesNotContain('o', room);
    }

    [Fact]
    public void Uzun_baslik_kisaltilir()
    {
        var url = MeetingLinks.CreateJitsiUrl(new string('a', 100));

        Assert.True(url.Length < 60, $"adres çok uzun: {url}");
    }

    [Fact]
    public void Sadece_isaretten_olusan_baslik_atlanir()
    {
        var url = MeetingLinks.CreateJitsiUrl("!!! ??? ...");

        Assert.DoesNotContain("--", url, StringComparison.Ordinal);
        Assert.DoesNotContain("/-", url, StringComparison.Ordinal);
    }

    // ==================================================================
    // Sağlayıcı tanıma
    // ==================================================================

    [Theory]
    [InlineData("https://teams.microsoft.com/l/meetup-join/abc", "teams")]
    [InlineData("https://teams.live.com/meet/123", "teams")]
    [InlineData("https://meet.google.com/abc-defg-hij", "meet")]
    [InlineData("https://firma.zoom.us/j/123456", "zoom")]
    [InlineData("https://firma.webex.com/meet/ali", "webex")]
    [InlineData("https://meet.jit.si/odam", "jitsi")]
    [InlineData("https://whereby.com/odam", "whereby")]
    [InlineData("https://baskabiryer.example/toplanti", "diger")]
    public void Saglayici_adresten_taninir(string url, string expected)
        => Assert.Equal(expected, MeetingLinks.DetectProvider(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bu bir adres değil")]
    public void Adres_olmayan_girdi_saglayici_uretmez(string? url)
        => Assert.Null(MeetingLinks.DetectProvider(url));

    [Fact]
    public void Benzer_adli_alan_teams_sayilmaz()
    {
        // "sahketeams.microsoft.com.kotu.example" tuzağı: son ek eşleşmesi
        // alan adının tamamına bakar.
        Assert.Equal("diger", MeetingLinks.DetectProvider("https://teams.microsoft.com.kotu.example/x"));
    }

    [Fact]
    public void Saglayici_adi_turkce_gosterilir()
    {
        Assert.Equal("Microsoft Teams", MeetingLinks.ProviderName("teams"));
        Assert.Equal("Çevrimiçi toplantı", MeetingLinks.ProviderName(null));
        Assert.Equal("Çevrimiçi toplantı", MeetingLinks.ProviderName("bilinmeyen"));
    }

    // ==================================================================
    // Harita
    // ==================================================================

    [Fact]
    public void Konum_harita_aramasina_cevrilir()
    {
        var url = MeetingLinks.MapSearchUrl("Kızılay Meydanı, Ankara");

        Assert.NotNull(url);
        Assert.Contains("query=", url, StringComparison.Ordinal);
        Assert.DoesNotContain(' ', url);
    }

    [Fact]
    public void Turkce_karakterli_konum_kacislanir()
    {
        var url = MeetingLinks.MapSearchUrl("Şişli")!;

        // Ham Türkçe harf adreste kalmamalı.
        Assert.DoesNotContain('Ş', url);
        Assert.Contains("%", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Konum_zaten_adresse_oldugu_gibi_kalir()
    {
        const string Source = "https://ornek.com/harita/1";

        Assert.Equal(Source, MeetingLinks.MapSearchUrl(Source));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Bos_konum_harita_baglantisi_uretmez(string? location)
        => Assert.Null(MeetingLinks.MapSearchUrl(location));
}
