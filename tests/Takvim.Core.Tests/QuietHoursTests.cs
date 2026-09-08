using NodaTime;
using Takvim.Core.Notifications;

namespace Takvim.Core.Tests;

/// <summary>
/// Sessiz saat penceresi. Asıl sınanan, gece yarısını aşan pencere: 22:00–08:00
/// iki ayrı güne yayılır ve "başlangıç &lt; bitiş" varsayan her karşılaştırma
/// burada yanlış cevap verir.
/// </summary>
public class QuietHoursTests
{
    private static LocalDateTime At(int hour, int minute = 0, int day = 2)
        => new(2026, 3, day, hour, minute);

    /// <summary>22:00–08:00, kapalıysa hiçbir şeyi susturmaz.</summary>
    private static QuietHours Night(bool enabled = true, bool onDaysOff = false)
        => new(enabled, new LocalTime(22, 0), new LocalTime(8, 0), onDaysOff);

    /// <summary>13:00–14:00, gün içinde kalan pencere.</summary>
    private static QuietHours Lunch(bool onDaysOff = false)
        => new(true, new LocalTime(13, 0), new LocalTime(14, 0), onDaysOff);

    // ==================================================================
    // Gün içinde kalan pencere
    // ==================================================================

    [Fact]
    public void Pencere_icindeki_an_sessizdir()
        => Assert.True(Lunch().Covers(At(13, 30)));

    [Fact]
    public void Pencere_disindaki_an_sessiz_degildir()
        => Assert.False(Lunch().Covers(At(12, 59)));

    [Fact]
    public void Baslangic_dahildir()
        => Assert.True(Lunch().Covers(At(13, 0)));

    /// <summary>
    /// Bitiş hariçtir: tam 14:00'te sessizlik biter. Tersi seçilseydi o dakikaya
    /// denk gelen hatırlatıcı kör noktaya düşerdi.
    /// </summary>
    [Fact]
    public void Bitis_haricdir()
        => Assert.False(Lunch().Covers(At(14, 0)));

    // ==================================================================
    // Gece yarısını aşan pencere
    // ==================================================================

    [Fact]
    public void Gece_penceresi_aksami_kapsar()
        => Assert.True(Night().Covers(At(23, 30)));

    [Fact]
    public void Gece_penceresi_sabahi_kapsar()
        => Assert.True(Night().Covers(At(3, 0)));

    [Fact]
    public void Gece_penceresi_gunduzu_kapsamaz()
        => Assert.False(Night().Covers(At(12, 0)));

    [Fact]
    public void Gece_penceresi_bitiste_biter()
        => Assert.False(Night().Covers(At(8, 0)));

    // ==================================================================
    // Kapalı ve boş pencere
    // ==================================================================

    [Fact]
    public void Kapaliyken_hicbir_an_sessiz_degildir()
        => Assert.False(Night(enabled: false).Covers(At(23, 30)));

    /// <summary>
    /// Başlangıç ve bitiş aynıysa pencere boştur; "bütün gün sessiz" diye
    /// yorumlanmaz. Yanlışlıkla aynı saati seçen kullanıcı bildirimlerini
    /// tümden kaybetmemelidir.
    /// </summary>
    [Fact]
    public void Ayni_baslangic_ve_bitis_bos_penceredir()
    {
        var window = new QuietHours(true, new LocalTime(9, 0), new LocalTime(9, 0), false);

        Assert.True(window.IsEmptyWindow);
        Assert.False(window.Covers(At(9, 0)));
        Assert.False(window.Covers(At(15, 0)));
    }

    // ==================================================================
    // Çalışılmayan gün
    // ==================================================================

    [Fact]
    public void Calisilmayan_gun_kurali_butun_gunu_susturur()
        => Assert.True(Night(onDaysOff: true).Covers(At(12, 0), isDayOff: true));

    [Fact]
    public void Calisilan_gunde_kural_devrede_degildir()
        => Assert.False(Night(onDaysOff: true).Covers(At(12, 0), isDayOff: false));

    [Fact]
    public void Kural_kapaliyken_calisilmayan_gun_de_sessiz_degildir()
        => Assert.False(Night(onDaysOff: false).Covers(At(12, 0), isDayOff: true));

    /// <summary>Sessiz saatler tümden kapalıysa çalışılmayan gün kuralı da işlemez.</summary>
    [Fact]
    public void Kapali_pencerede_calisilmayan_gun_kurali_islemez()
    {
        var window = new QuietHours(false, new LocalTime(22, 0), new LocalTime(8, 0), true);

        Assert.False(window.Covers(At(12, 0), isDayOff: true));
    }

    // ==================================================================
    // Sessizliğin bitişi
    // ==================================================================

    [Fact]
    public void Sessiz_degilken_bitis_yoktur()
        => Assert.Null(Night().EndsAfter(At(12, 0)));

    [Fact]
    public void Gunduz_penceresi_ayni_gun_biter()
        => Assert.Equal(At(14, 0), Lunch().EndsAfter(At(13, 30)));

    [Fact]
    public void Gece_penceresi_aksamdan_ertesi_sabaha_surer()
        => Assert.Equal(At(8, 0, day: 3), Night().EndsAfter(At(23, 30)));

    [Fact]
    public void Gece_penceresi_sabah_ayni_gun_biter()
        => Assert.Equal(At(8, 0), Night().EndsAfter(At(3, 0)));

    /// <summary>
    /// Çalışılmayan günde gündüz sessizliği gün sonuna kadar sürer: pencere o
    /// anı kapsamıyor, susturan kural bütün gün geçerli olan diğeridir.
    /// </summary>
    [Fact]
    public void Calisilmayan_gunde_sessizlik_gun_sonuna_surer()
        => Assert.Equal(At(0, 0, day: 3), Night(onDaysOff: true).EndsAfter(At(12, 0), isDayOff: true));

    /// <summary>
    /// İki kural birden geçerliyse sessizlik geç bitenle biter. Cumartesi
    /// 23:30'da gün sonu yaklaşmıştır ama gece penceresi sabah 08:00'e kadar sürer.
    /// </summary>
    [Fact]
    public void Iki_kural_cakisinca_gec_biten_kazanir()
        => Assert.Equal(At(8, 0, day: 3), Night(onDaysOff: true).EndsAfter(At(23, 30), isDayOff: true));
}
