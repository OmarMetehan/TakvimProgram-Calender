using NodaTime;
using Takvim.Core.Localization;

namespace Takvim.Core.Tests;

public class TurkishEventParserTests
{
    /// <summary>Pazartesi, 2 Mart 2026, saat 10:15. Tüm göreli ifadeler buna göre çözülür.</summary>
    private static readonly LocalDateTime Now = new(2026, 3, 2, 10, 15);

    private static ParsedEvent Parse(string input) => TurkishEventParser.Parse(input, Now);

    // ------------------------------------------------------------------
    // Kılavuzdaki örnek
    // ------------------------------------------------------------------

    [Fact]
    public void Persembe_saat_ve_baslik_ayristirilir()
    {
        var result = Parse("perşembe 14:00 Ahmet ile toplantı");

        Assert.Equal(new LocalDateTime(2026, 3, 5, 14, 0), result.Start);
        Assert.Equal(new LocalDateTime(2026, 3, 5, 15, 0), result.End);
        Assert.Equal("Ahmet ile toplantı", result.Title);
        Assert.True(result.RecognizedSchedule);
    }

    // ------------------------------------------------------------------
    // Göreli günler
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("bugün 14:00 spor", 2)]
    [InlineData("yarın 14:00 spor", 3)]
    [InlineData("öbür gün 14:00 spor", 4)]
    public void Goreli_gunler_cozulur(string input, int expectedDay)
    {
        var result = Parse(input);

        Assert.Equal(new LocalDate(2026, 3, expectedDay), result.Start.Date);
        Assert.Equal("Spor", result.Title);
    }

    [Fact]
    public void Ayni_gunun_adi_verilirse_bugun_secilir()
    {
        // Bugün pazartesi; "pazartesi" bugünü işaret eder, gelecek haftayı değil.
        Assert.Equal(new LocalDate(2026, 3, 2), Parse("pazartesi 09:00 toplantı").Start.Date);
    }

    [Fact]
    public void Gelecek_eki_bir_hafta_ileri_atar()
    {
        Assert.Equal(new LocalDate(2026, 3, 9), Parse("gelecek pazartesi 09:00 toplantı").Start.Date);
        Assert.Equal(new LocalDate(2026, 3, 12), Parse("önümüzdeki perşembe 09:00 toplantı").Start.Date);
    }

    [Fact]
    public void Turkce_ekler_yutulur()
    {
        // "perşembeye", "yarınki" gibi çekimli biçimler de tanınmalı.
        Assert.Equal(new LocalDate(2026, 3, 5), Parse("perşembeye 14:00 kontrol").Start.Date);
        Assert.Equal("Kontrol", Parse("perşembeye 14:00 kontrol").Title);
    }

    // ------------------------------------------------------------------
    // Açık tarihler
    // ------------------------------------------------------------------

    [Fact]
    public void Ay_adiyla_tarih_cozulur()
    {
        var result = Parse("15 Mart 09:00 sunum");

        Assert.Equal(new LocalDate(2026, 3, 15), result.Start.Date);
        Assert.Equal("Sunum", result.Title);
    }

    [Fact]
    public void Yil_yazilmamis_gecmis_tarih_gelecek_yila_atilir()
    {
        // Bugün 2 Mart 2026; "1 Şubat" geçmişte kaldığı için 2027 kastedilmiştir.
        Assert.Equal(new LocalDate(2027, 2, 1), Parse("1 Şubat 09:00 planlama").Start.Date);
    }

    [Fact]
    public void Acik_yil_oldugu_gibi_kullanilir()
        => Assert.Equal(new LocalDate(2027, 3, 15), Parse("15 Mart 2027 09:00 sunum").Start.Date);

    [Fact]
    public void Noktali_tarih_bicimi_cozulur()
    {
        Assert.Equal(new LocalDate(2026, 4, 15), Parse("15.04.2026 09:00 sunum").Start.Date);
        Assert.Equal(new LocalDate(2026, 4, 15), Parse("15.04 09:00 sunum").Start.Date);
    }

    // ------------------------------------------------------------------
    // Saat ve süre
    // ------------------------------------------------------------------

    [Fact]
    public void Saat_araligi_cozulur()
    {
        var result = Parse("yarın 14:00-15:30 bütçe görüşmesi");

        Assert.Equal(new LocalDateTime(2026, 3, 3, 14, 0), result.Start);
        Assert.Equal(new LocalDateTime(2026, 3, 3, 15, 30), result.End);
        Assert.Equal("Bütçe görüşmesi", result.Title);
    }

    [Fact]
    public void Geceyi_asan_aralik_ertesi_gune_tasar()
    {
        var result = Parse("yarın 23:00-01:00 nöbet");

        Assert.Equal(new LocalDateTime(2026, 3, 3, 23, 0), result.Start);
        Assert.Equal(new LocalDateTime(2026, 3, 4, 1, 0), result.End);
    }

    [Fact]
    public void Sure_ifadesi_bitisi_belirler()
    {
        var result = Parse("yarın 14:00 2 saat atölye");

        Assert.Equal(new LocalDateTime(2026, 3, 3, 16, 0), result.End);
        Assert.Equal("Atölye", result.Title);
    }

    [Fact]
    public void Ondalikli_sure_desteklenir()
        => Assert.Equal(new LocalDateTime(2026, 3, 3, 15, 30), Parse("yarın 14:00 1,5 saat atölye").End);

    [Fact]
    public void Dakika_cinsinden_sure_desteklenir()
        => Assert.Equal(new LocalDateTime(2026, 3, 3, 14, 45), Parse("yarın 14:00 45 dakika görüşme").End);

    [Fact]
    public void Saat_kelimesiyle_yazilan_saat_cozulur()
    {
        var result = Parse("yarın saat 14 toplantı");

        Assert.Equal(new LocalDateTime(2026, 3, 3, 14, 0), result.Start);
        Assert.Equal("Toplantı", result.Title);
    }

    // ------------------------------------------------------------------
    // Tüm gün
    // ------------------------------------------------------------------

    [Fact]
    public void Tum_gun_ifadesi_tanınır()
    {
        var result = Parse("5 Nisan tüm gün tatil");

        Assert.True(result.IsAllDay);
        Assert.Equal(new LocalDate(2026, 4, 5), result.Start.Date);
        Assert.Equal(new LocalDate(2026, 4, 6), result.End.Date);
        Assert.Equal("Tatil", result.Title);
    }

    // ------------------------------------------------------------------
    // Tekrar
    // ------------------------------------------------------------------

    [Fact]
    public void Her_gun_kurali_uretilir()
        => Assert.Equal("FREQ=DAILY", Parse("her gün 09:00 yürüyüş").RecurrenceRule);

    [Fact]
    public void Her_belirli_gun_kurali_uretilir()
    {
        var result = Parse("her pazartesi 09:00 ekip toplantısı");

        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", result.RecurrenceRule);
        Assert.Equal("Ekip toplantısı", result.Title);
    }

    [Fact]
    public void Hafta_ici_her_gun_kurali_uretilir()
        => Assert.Equal("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR",
            Parse("hafta içi her gün 08:30 kahve").RecurrenceRule);

    [Fact]
    public void Aralikli_tekrar_kurali_uretilir()
    {
        Assert.Equal("FREQ=WEEKLY;INTERVAL=2", Parse("iki haftada bir 10:00 rapor").RecurrenceRule);
        Assert.Equal("FREQ=MONTHLY;INTERVAL=3", Parse("3 ayda bir 10:00 değerlendirme").RecurrenceRule);
    }

    [Fact]
    public void Her_gun_ifadesi_gun_adiyla_karistirilmaz()
    {
        // "her pazartesi" tekrar kuralıdır; ayrıca tarih olarak da okunmamalıdır.
        var result = Parse("her pazartesi 09:00 toplantı");

        Assert.Equal("FREQ=WEEKLY;BYDAY=MO", result.RecurrenceRule);
        Assert.Equal("Toplantı", result.Title);
    }

    // ------------------------------------------------------------------
    // Varsayılanlar ve kenar durumlar
    // ------------------------------------------------------------------

    [Fact]
    public void Sadece_baslik_yazilirsa_varsayilan_zaman_kullanilir()
    {
        var result = Parse("Kitap okuma");

        Assert.False(result.RecognizedSchedule);
        Assert.Equal("Kitap okuma", result.Title);
        Assert.Equal(Now.Date, result.Start.Date);
        // Şu an 10:15; sonraki yarım saatlik dilim 10:30'dur.
        Assert.Equal(new LocalTime(10, 30), result.Start.TimeOfDay);
    }

    [Fact]
    public void Saatsiz_tarih_varsayilan_saati_alir()
    {
        var result = Parse("yarın diş hekimi");

        Assert.Equal(new LocalDateTime(2026, 3, 3, 9, 0), result.Start);
        Assert.Equal("Diş hekimi", result.Title);
    }

    [Fact]
    public void Bos_girdi_cokme_yapmaz()
    {
        var result = Parse("");

        Assert.Equal(string.Empty, result.Title);
        Assert.False(result.RecognizedSchedule);
    }

    [Fact]
    public void Baslik_ilk_harfi_turkce_kuralla_buyutulur()
    {
        // Türkçe'de "i" harfinin büyüğü "İ"dir; değişmez kültür "I" üretirdi.
        Assert.Equal("İzin günü", Parse("yarın izin günü").Title);
    }

    [Fact]
    public void Bastaki_baglac_baslikta_kalmaz()
        => Assert.Equal("Ahmet ile görüşme", Parse("perşembe 14:00 için Ahmet ile görüşme").Title);
}
