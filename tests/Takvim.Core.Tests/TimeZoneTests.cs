using NodaTime;
using Takvim.Core.Recurrence;
using Takvim.Core.Time;
using static Takvim.Core.Tests.TestEvents;

namespace Takvim.Core.Tests;

/// <summary>
/// Zaman dilimi altın testleri. Buradaki vakaların her biri, etkinlikleri UTC olarak
/// saklayan uygulamalarda gerçekten yaşanmış bir veri kayması sınıfıdır.
/// </summary>
public class TimeZoneTests
{
    private readonly TimeZoneService _zones = new();
    private readonly RecurrenceExpander _expander = new(Zones);

    // ------------------------------------------------------------------
    // Türkiye'nin kalıcı UTC+3 geçişi
    // ------------------------------------------------------------------

    [Fact]
    public void Turkiye_2016_oncesi_yaz_saati_uyguluyordu()
    {
        // 2016 yazı: UTC+3 (yaz saati). 2016 kışı: UTC+2.
        Assert.Equal(Offset.FromHours(3), _zones.OffsetAt(Utc("2016-07-01 12:00"), Istanbul));
        Assert.Equal(Offset.FromHours(2), _zones.OffsetAt(Utc("2016-01-15 12:00"), Istanbul));
    }

    [Fact]
    public void Turkiye_2016_sonrasi_kalici_utc_arti_uc()
    {
        // 7 Eylül 2016'dan sonra kış saatine dönülmedi; kışın da UTC+3.
        Assert.Equal(Offset.FromHours(3), _zones.OffsetAt(Utc("2017-01-15 12:00"), Istanbul));
        Assert.Equal(Offset.FromHours(3), _zones.OffsetAt(Utc("2026-01-15 12:00"), Istanbul));
    }

    [Fact]
    public void Kural_degisikliginden_once_kaydedilen_etkinlik_yerel_saatini_korur()
    {
        // Bu, yerel saat + zaman dilimi saklamanın asıl gerekçesidir.
        // 2015 kışında saat 14:00'e kurulmuş bir etkinlik, kurallar değiştikten sonra
        // okunduğunda hâlâ 14:00 olmalıdır; UTC saklansaydı 15:00'e kayardı.
        var ev = Timed("2015-01-15 14:00", "2015-01-15 15:00");

        var yerel = _zones.ToLocal(ev.StartUtc, Istanbul);

        Assert.Equal(14, yerel.Hour);
        Assert.Equal(Offset.FromHours(2), _zones.OffsetAt(ev.StartUtc, Istanbul));
    }

    // ------------------------------------------------------------------
    // Yaz saati boşluğu ve belirsizliği
    // ------------------------------------------------------------------

    [Fact]
    public void Ileri_alinan_saatte_var_olmayan_yerel_saat_tespit_edilir()
    {
        // Berlin, 29 Mart 2026: 02:00 -> 03:00. 02:30 hiç yaşanmaz.
        Assert.Equal(LocalResolution.Skipped, _zones.Resolve(Parse("2026-03-29 02:30"), Berlin));
        Assert.Equal(LocalResolution.Unambiguous, _zones.Resolve(Parse("2026-03-29 04:30"), Berlin));
    }

    [Fact]
    public void Var_olmayan_yerel_saat_bosluk_kadar_ileri_kaydirilir()
    {
        var instant = _zones.ToInstant(Parse("2026-03-29 02:30"), Berlin);

        // Boşluk 02:00-03:00 arasıdır ve bir saattir. 02:30 bir saat ileri kaydırılarak
        // 03:30 olur; geçişin ilk anına yığılmaz. Böylece aynı gün 02:15 ve 02:45'e
        // kurulmuş iki etkinlik çakışmaz, aralarındaki mesafe korunur.
        Assert.Equal(new LocalDateTime(2026, 3, 29, 3, 30), _zones.ToLocal(instant, Berlin));
    }

    [Fact]
    public void Bosluga_dusen_iki_ayri_saat_cakismaz()
    {
        var ilk = _zones.ToInstant(Parse("2026-03-29 02:15"), Berlin);
        var ikinci = _zones.ToInstant(Parse("2026-03-29 02:45"), Berlin);

        Assert.NotEqual(ilk, ikinci);
        Assert.Equal(Duration.FromMinutes(30), ikinci - ilk);
    }

    [Fact]
    public void Geri_alinan_saatte_iki_kez_yasanan_yerel_saat_tespit_edilir()
    {
        // Berlin, 25 Ekim 2026: 03:00 -> 02:00. 02:30 iki kez yaşanır.
        Assert.Equal(LocalResolution.Ambiguous, _zones.Resolve(Parse("2026-10-25 02:30"), Berlin));
    }

    [Fact]
    public void Belirsiz_yerel_saatte_ilk_yasanan_secilir()
    {
        var instant = _zones.ToInstant(Parse("2026-10-25 02:30"), Berlin);

        // İlk 02:30 hâlâ yaz saatindedir: UTC+2.
        Assert.Equal(Offset.FromHours(2), _zones.OffsetAt(instant, Berlin));
    }

    // ------------------------------------------------------------------
    // Tekrarlayan etkinliklerin yaz saati davranışı
    // ------------------------------------------------------------------

    [Fact]
    public void Gunluk_toplanti_yaz_saati_gecisinde_ayni_duvar_saatinde_kalir()
    {
        // Berlin'de her sabah 09:00 toplantı; 29 Mart 2026 geçişinden sonra da 09:00 olmalı.
        var ev = Timed("2026-03-27 09:00", "2026-03-27 10:00", "FREQ=DAILY", tzId: Berlin);

        var occurrences = _expander
            .Expand(ev, null, _zones.ToInstant(Parse("2026-03-27 00:00"), Berlin),
                              _zones.ToInstant(Parse("2026-03-31 00:00"), Berlin))
            .OrderBy(o => o.StartUtc)
            .ToList();

        Assert.All(occurrences, o => Assert.Equal(9, o.StartLocal.Hour));

        // Ama mutlak an kayar: geçişten önce 08:00 UTC, sonra 07:00 UTC.
        Assert.Equal(Offset.FromHours(1), _zones.OffsetAt(occurrences[0].StartUtc, Berlin));
        Assert.Equal(Offset.FromHours(2), _zones.OffsetAt(occurrences[^1].StartUtc, Berlin));
    }

    [Fact]
    public void Gecis_gunundeki_toplantinin_suresi_duvar_saatine_gore_korunur()
    {
        // 09:00-10:00 toplantı, geçiş gününde de 09:00-10:00 görünmelidir.
        var ev = Timed("2026-03-27 09:00", "2026-03-27 10:00", "FREQ=DAILY", tzId: Berlin);

        var gecisGunu = _expander
            .Expand(ev, null, _zones.ToInstant(Parse("2026-03-29 00:00"), Berlin),
                              _zones.ToInstant(Parse("2026-03-30 00:00"), Berlin))
            .Single();

        Assert.Equal(new LocalDateTime(2026, 3, 29, 9, 0), gecisGunu.StartLocal);
        Assert.Equal(new LocalDateTime(2026, 3, 29, 10, 0), gecisGunu.EndLocal);
        Assert.Equal(Duration.FromHours(1), gecisGunu.EndUtc - gecisGunu.StartUtc);
    }

    [Fact]
    public void Gecis_araligini_kapsayan_toplanti_mutlak_olarak_bir_saat_kisalir()
    {
        // 01:00-04:00 arası bir toplantı, saatler ileri alındığı gün gerçekte 2 saat sürer.
        // Duvar saati 01:00-04:00 kalır; mutlak süre kısalır. Doğru davranış budur.
        var ev = Timed("2026-03-29 01:00", "2026-03-29 04:00", tzId: Berlin);

        Assert.Equal(Duration.FromHours(2), ev.EndUtc - ev.StartUtc);
    }

    // ------------------------------------------------------------------
    // Farklı başlangıç ve bitiş dilimi
    // ------------------------------------------------------------------

    [Fact]
    public void Ucus_farkli_dilimlerde_baslayip_biter()
    {
        // İstanbul 10:00'da kalkış, Berlin 12:00'de iniş. Gerçek süre 3 saattir,
        // çünkü Berlin İstanbul'dan iki saat geridedir.
        var ev = Timed("2026-06-01 10:00", "2026-06-01 12:00", tzId: Istanbul, endTzId: Berlin);

        Assert.Equal(Duration.FromHours(3), ev.EndUtc - ev.StartUtc);
    }

    // ------------------------------------------------------------------
    // Tüm gün etkinlikleri
    // ------------------------------------------------------------------

    [Fact]
    public void Tum_gun_etkinliginin_zaman_dilimi_yoktur()
    {
        var ev = AllDay("2026-06-01");

        Assert.Null(ev.StartTimeZoneId);
        Assert.Equal(new LocalDateTime(2026, 6, 1, 0, 0), ev.StartLocal);
        // Bitiş dışlayıcıdır: tek günlük etkinlik ertesi günün gece yarısında biter.
        Assert.Equal(new LocalDateTime(2026, 6, 2, 0, 0), ev.EndLocal);
    }

    // ------------------------------------------------------------------
    // Ortam bağımsızlığı
    // ------------------------------------------------------------------

    [Fact]
    public void Zaman_dilimi_veritabani_yuklu_ve_istanbul_taniniyor()
    {
        Assert.True(_zones.IsKnown(Istanbul));
        Assert.True(_zones.IsKnown("America/New_York"));
        Assert.False(_zones.IsKnown("Turkey/Istanbul"));
        Assert.False(string.IsNullOrWhiteSpace(_zones.TzdbVersion));
    }

    [Fact]
    public void Bilinmeyen_zaman_dilimi_varsayilana_duser()
    {
        // Bozuk veri uygulamayı çökertmemeli, öngörülebilir biçimde varsayılana düşmeli.
        Assert.Equal(_zones.Get(Istanbul), _zones.Get("Yok/Boyle/Bir/Yer"));
    }
}
