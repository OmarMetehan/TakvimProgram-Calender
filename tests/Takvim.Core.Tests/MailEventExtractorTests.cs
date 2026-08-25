using NodaTime;
using Takvim.Core.Mail;

namespace Takvim.Core.Tests;

/// <summary>
/// E-postadan etkinlik çıkarma. Asıl sınanan şey doğru cümleyi seçmek:
/// postanın tamamını ayrıştırıcıya vermek, imzada ve alıntılanmış yanıtta
/// geçen her sayının yanlış bir tarih üretmesine yol açar.
/// </summary>
public class MailEventExtractorTests
{
    // 2026-03-02 pazartesi.
    private static readonly LocalDateTime Now = new(2026, 3, 2, 9, 0);

    private static ExtractedEvent? Extract(string? subject, string? body)
        => MailEventExtractor.Extract(subject, body, Now);

    // ==================================================================
    // Tarih bulma
    // ==================================================================

    [Fact]
    public void Govdedeki_tarih_bulunur()
    {
        var found = Extract("Bütçe", "Merhaba, çarşamba 14:00'te görüşelim mi?");

        Assert.NotNull(found);
        Assert.True(found.RecognizedSchedule);
        Assert.Equal(new LocalDateTime(2026, 3, 4, 14, 0), found.Start);
    }

    [Fact]
    public void Konu_satirindaki_tarih_de_bulunur()
    {
        // Toplantı davetlerinde tarih sık sık konudadır.
        var found = Extract("Toplantı — perşembe 10:00", "Ekte sunum var.");

        Assert.NotNull(found);
        Assert.True(found.RecognizedSchedule);
        Assert.Equal(new LocalDateTime(2026, 3, 5, 10, 0), found.Start);
    }

    [Fact]
    public void Govde_konudan_once_gelir()
    {
        // Gövdedeki saat daha güvenilirdir: konu eski bir yanıttan kalmış olabilir.
        var found = Extract("Toplantı — pazartesi 10:00", "Salı 15:00'te buluşalım.");

        Assert.Equal(new LocalDateTime(2026, 3, 3, 15, 0), found!.Start);
    }

    [Fact]
    public void Baslik_konu_satirindan_alinir()
    {
        // Ayrıştırıcının cümleden arta bıraktığı metin yarım kalır; konu zaten bir başlıktır.
        var found = Extract("Bütçe görüşmesi", "Çarşamba 14:00 uygun mu?");

        Assert.Equal("Bütçe görüşmesi", found!.Title);
    }

    [Theory]
    [InlineData("RE: Bütçe")]
    [InlineData("Fw: Bütçe")]
    [InlineData("YAN: Bütçe")]
    [InlineData("RE: FW: Bütçe")]
    public void Yanit_ekleri_baslikta_kalmaz(string subject)
    {
        var found = Extract(subject, "Çarşamba 14:00'te.");

        Assert.Equal("Bütçe", found!.Title);
    }

    // ==================================================================
    // Budama
    // ==================================================================

    [Fact]
    public void Alintilanan_yanit_okunmaz()
    {
        // Alıntının içindeki tarih geçmişe aittir.
        var body = """
            Yarın uygun değilim.

            Kimden: Ali
            Salı 09:00'da toplanalım.
            """;

        var found = Extract("Toplantı", body);

        // "Yarın" 3 Mart; alıntıdaki salı 09:00 seçilmemeli.
        Assert.Equal(new LocalDate(2026, 3, 3), found!.Start.Date);
    }

    [Theory]
    [InlineData("yarın uygun değilim")]
    [InlineData("Yarın uygun değilim")]
    [InlineData("YARIN uygun değilim")]
    [InlineData("yarin uygun degilim")]
    public void Goreli_gun_adi_yazimdan_bagimsiz_taninir(string body)
    {
        // Cümle başındaki "Yarın" da yarın demektir; e-postada büyük harfle gelir.
        // Aksansız "yarin" de aynı şeydir.
        var found = Extract("Toplantı", body);

        Assert.Equal(new LocalDate(2026, 3, 3), found!.Start.Date);
    }

    [Fact]
    public void Ingilizce_alinti_isareti_de_taninir()
    {
        var body = """
            Perşembe 16:00 olsun.

            On Mon, Ali wrote:
            Pazartesi 08:00 demiştik.
            """;

        var found = Extract("Toplantı", body);

        Assert.Equal(new LocalDateTime(2026, 3, 5, 16, 0), found!.Start);
    }

    [Fact]
    public void Imza_okunmaz()
    {
        var body = "Cuma 11:00 uygun.\n-- \nAhmet Yılmaz\nTel: 0212 555 12 34";

        var found = Extract("Görüşme", body);

        Assert.Equal(new LocalDateTime(2026, 3, 6, 11, 0), found!.Start);
    }

    // ==================================================================
    // Ek alanlar
    // ==================================================================

    [Theory]
    [InlineData("Yer: Toplantı Odası 3")]
    [InlineData("Konum: Toplantı Odası 3")]
    [InlineData("Location: Toplantı Odası 3")]
    public void Konum_satiri_okunur(string line)
    {
        var found = Extract("Toplantı", $"Çarşamba 14:00.\n{line}\nGörüşmek üzere.");

        Assert.Equal("Toplantı Odası 3", found!.LocationText);
    }

    [Fact]
    public void Toplanti_baglantisi_bulunur()
    {
        var body = "Perşembe 10:00.\nKatılım: https://meet.google.com/abc-defg-hij";

        var found = Extract("Toplantı", body);

        Assert.Equal("https://meet.google.com/abc-defg-hij", found!.OnlineMeetingUrl);
    }

    [Fact]
    public void Taninmayan_adres_toplanti_baglantisi_sayilmaz()
    {
        var body = "Perşembe 10:00.\nDetay: https://sirket.example/duyuru";

        Assert.Null(Extract("Toplantı", body)!.OnlineMeetingUrl);
    }

    [Fact]
    public void Baglantinin_sonundaki_noktalama_atilir()
    {
        var body = "Perşembe 10:00. Adres https://meet.jit.si/odam.";

        Assert.Equal("https://meet.jit.si/odam", Extract("Toplantı", body)!.OnlineMeetingUrl);
    }

    [Fact]
    public void Dayanak_cumlesi_saklanir()
    {
        var found = Extract("Bütçe", "Merhaba.\nÇarşamba 14:00'te görüşelim.\nİyi günler.");

        Assert.NotNull(found!.Evidence);
        Assert.Contains("14:00", found.Evidence, StringComparison.Ordinal);
    }

    // ==================================================================
    // Öneri üretmeme
    // ==================================================================

    [Theory]
    [InlineData("Fatura", "Ekteki faturayı inceleyebilir misiniz?")]
    [InlineData("Bülten", "Bu haftanın öne çıkanları aşağıda.")]
    [InlineData(null, null)]
    [InlineData("", "")]
    public void Etkinlik_izi_olmayan_posta_oneri_uretmez(string? subject, string? body)
        => Assert.Null(Extract(subject, body));

    [Fact]
    public void Saatsiz_toplanti_sozu_oneri_uretir_ama_isaretlenir()
    {
        // Tarih yok ama toplantı sözü var: kullanıcı saati kendi seçsin.
        var found = Extract("Toplantı talebi", "Bir toplantı ayarlayabilir miyiz?");

        Assert.NotNull(found);
        Assert.False(found.RecognizedSchedule);
    }

    [Theory]
    [InlineData("görüşme")]
    [InlineData("randevu")]
    [InlineData("buluşma")]
    [InlineData("davet")]
    public void Toplanti_esanlamlilari_taninir(string word)
    {
        Assert.NotNull(Extract($"Bir {word} hakkında", "Detaylar aşağıda."));
    }

    // ==================================================================
    // Yardımcılar
    // ==================================================================

    [Fact]
    public void Baglanti_bulucu_bos_metinde_null_doner()
    {
        Assert.Null(MailEventExtractor.FindMeetingUrl(null));
        Assert.Null(MailEventExtractor.FindMeetingUrl("   "));
    }

    [Fact]
    public void Konum_bulucu_bos_metinde_null_doner()
    {
        Assert.Null(MailEventExtractor.FindLocation(null));
        Assert.Null(MailEventExtractor.FindLocation("konumdan hiç söz edilmiyor"));
    }

    [Fact]
    public void Cok_uzun_cumle_atlanir()
    {
        // 200 karakterden uzun bir satır gürültüdür; tarih taşısa da okunmaz.
        var noise = new string('a', 250) + " çarşamba 14:00";

        Assert.Null(Extract("Bülten", noise));
    }

    [Fact]
    public void Baslik_bossa_varsayilan_kullanilir()
    {
        var found = Extract(null, "Çarşamba 14:00'te toplantı var.");

        Assert.NotNull(found);
        Assert.False(string.IsNullOrWhiteSpace(found.Title));
    }
}
