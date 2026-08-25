using NodaTime;
using Takvim.Core.Localization;
using Takvim.Core.Time;

namespace Takvim.Core.Tests;

/// <summary>
/// Izgaradaki ikinci zaman dilimi sütunu. İki şey kolay hata verir ve ikisi de
/// burada sınanır: gün kayması (23:00 İstanbul, Tokyo'da ertesi gün) ve tam
/// saat olmayan farklar (Kolkata yarım saat kaymıştır).
/// </summary>
public class SecondaryZoneTests
{
    private readonly TimeZoneService _zones = new();

    /// <summary>Birincil dilimdeki bir saatin ikincil dilimdeki etiketi.</summary>
    private string Label(string primary, string secondary, LocalDate date, int hour)
    {
        var instant = _zones.ToInstant(date.At(new LocalTime(hour, 0)), primary);
        var local = _zones.ToLocal(instant, secondary);

        return TurkishFormat.SecondaryHour(local, date);
    }

    // ==================================================================
    // Aynı gün
    // ==================================================================

    [Fact]
    public void Ayni_gundeki_karsilik_isaretsiz_gosterilir()
    {
        // 2026-03-02 kışın: İstanbul UTC+3, Londra UTC+0.
        var label = Label("Europe/Istanbul", "Europe/London", new LocalDate(2026, 3, 2), 12);

        Assert.Equal("09:00", label);
    }

    [Fact]
    public void Kendi_dilimi_ayni_saati_verir()
        => Assert.Equal("14:00",
            Label("Europe/Istanbul", "Europe/Istanbul", new LocalDate(2026, 3, 2), 14));

    // ==================================================================
    // Gün kayması
    // ==================================================================

    [Fact]
    public void Ertesi_gune_tasan_saat_isaretlenir()
    {
        // 23:00 İstanbul = 05:00 Tokyo, ertesi gün.
        var label = Label("Europe/Istanbul", "Asia/Tokyo", new LocalDate(2026, 3, 2), 23);

        Assert.Equal("05:00⁺", label);
    }

    [Fact]
    public void Onceki_gune_dusen_saat_isaretlenir()
    {
        // 2 Mart'ta New York henüz yaz saatine geçmemiştir (UTC-5), İstanbul
        // UTC+3: fark sekiz saat. 01:00 İstanbul = 17:00 New York, önceki gün.
        var label = Label("Europe/Istanbul", "America/New_York", new LocalDate(2026, 3, 2), 1);

        Assert.Equal("17:00⁻", label);
    }

    [Fact]
    public void Isaret_yalnizca_gun_degisince_konur()
    {
        // Aynı gün içindeki bir saat işaretsiz kalmalı.
        var label = Label("Europe/Istanbul", "Asia/Tokyo", new LocalDate(2026, 3, 2), 10);

        Assert.Equal("16:00", label);
        Assert.DoesNotContain("⁺", label, StringComparison.Ordinal);
    }

    // ==================================================================
    // Tam saat olmayan farklar
    // ==================================================================

    [Fact]
    public void Yarim_saatlik_fark_dakikayla_gosterilir()
    {
        // Kolkata UTC+5:30; İstanbul UTC+3 iken fark 2,5 saattir.
        var label = Label("Europe/Istanbul", "Asia/Kolkata", new LocalDate(2026, 3, 2), 12);

        Assert.Equal("14:30", label);
    }

    // ==================================================================
    // Yaz saati
    // ==================================================================

    [Fact]
    public void Yaz_saatinde_fark_degisir()
    {
        // İstanbul yaz saati uygulamaz, Londra uygular. Kışın fark 3 saat,
        // yazın 2 saattir; sütun buna göre değişmeli.
        var winter = Label("Europe/Istanbul", "Europe/London", new LocalDate(2026, 1, 15), 12);
        var summer = Label("Europe/Istanbul", "Europe/London", new LocalDate(2026, 7, 15), 12);

        Assert.Equal("09:00", winter);
        Assert.Equal("10:00", summer);
    }

    // ==================================================================
    // Biçimlendirme
    // ==================================================================

    [Fact]
    public void Saat_iki_haneli_yazilir()
    {
        var label = Label("Europe/Istanbul", "Europe/London", new LocalDate(2026, 3, 2), 10);

        Assert.Equal("07:00", label);
    }

    [Theory]
    [InlineData(0, 0, "")]
    [InlineData(1, 0, "⁺")]
    [InlineData(-1, 0, "⁻")]
    public void Gun_farki_dogru_isaretlenir(int dayOffset, int hour, string expected)
    {
        var reference = new LocalDate(2026, 3, 2);
        var local = reference.PlusDays(dayOffset).At(new LocalTime(hour, 0));

        Assert.EndsWith(expected, TurkishFormat.SecondaryHour(local, reference), StringComparison.Ordinal);
    }
}
