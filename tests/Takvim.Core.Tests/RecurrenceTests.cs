using Takvim.Core.Recurrence;
using static Takvim.Core.Tests.TestEvents;

namespace Takvim.Core.Tests;

/// <summary>
/// Tekrarlama motorunun altın testleri. Buradaki her vaka, gerçek takvim
/// uygulamalarında bilinen bir hata sınıfını temsil eder; kural motoruna
/// dokunulduğunda ilk bunlar çalıştırılmalıdır.
/// </summary>
public class RecurrenceTests
{
    private readonly RecurrenceExpander _expander = new(Zones);

    private string[] Expand(Takvim.Core.Domain.Event root, string from, string to,
                            params Takvim.Core.Domain.Event[] exceptions)
        => _expander.Expand(root, exceptions, Utc(from), Utc(to)).OrderBy(o => o.StartUtc).ToArray().Starts();

    // ------------------------------------------------------------------
    // Temel frekanslar
    // ------------------------------------------------------------------

    [Fact]
    public void Gunluk_tekrar_pencere_icindeki_ornekleri_uretir()
    {
        var ev = Timed("2026-03-02 09:00", "2026-03-02 10:00", "FREQ=DAILY");

        var result = Expand(ev, "2026-03-02 00:00", "2026-03-05 00:00");

        Assert.Equal(["2026-03-02 09:00", "2026-03-03 09:00", "2026-03-04 09:00"], result);
    }

    [Fact]
    public void Haftalik_belirli_gunlerde_tekrarlar()
    {
        var ev = Timed("2026-03-02 09:00", "2026-03-02 10:00", "FREQ=WEEKLY;BYDAY=MO,WE,FR");

        var result = Expand(ev, "2026-03-02 00:00", "2026-03-09 00:00");

        Assert.Equal(["2026-03-02 09:00", "2026-03-04 09:00", "2026-03-06 09:00"], result);
    }

    [Fact]
    public void Her_ayin_ucuncu_salisi()
    {
        var ev = Timed("2026-01-20 14:00", "2026-01-20 15:00", "FREQ=MONTHLY;BYDAY=3TU");

        var result = Expand(ev, "2026-01-01 00:00", "2026-05-01 00:00");

        Assert.Equal(
            ["2026-01-20 14:00", "2026-02-17 14:00", "2026-03-17 14:00", "2026-04-21 14:00"],
            result);
    }

    [Fact]
    public void Iki_haftada_bir_pazartesi_ve_carsamba()
    {
        var ev = Timed("2026-03-02 09:00", "2026-03-02 10:00", "FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE");

        var result = Expand(ev, "2026-03-02 00:00", "2026-04-01 00:00");

        // 2 ve 4 Mart; sonraki hafta atlanır; 16 ve 18 Mart; 30 Mart.
        Assert.Equal(
            ["2026-03-02 09:00", "2026-03-04 09:00", "2026-03-16 09:00", "2026-03-18 09:00", "2026-03-30 09:00"],
            result);
    }

    [Fact]
    public void Ayin_31i_kisa_aylari_atlar()
    {
        var ev = Timed("2026-01-31 09:00", "2026-01-31 10:00", "FREQ=MONTHLY;BYMONTHDAY=31");

        var result = Expand(ev, "2026-01-01 00:00", "2026-06-01 00:00");

        // Şubat, nisan ve haziranda 31 yoktur; o aylar üretilmez.
        Assert.Equal(["2026-01-31 09:00", "2026-03-31 09:00", "2026-05-31 09:00"], result);
    }

    [Fact]
    public void Yillik_29_subat_yalnizca_artik_yillarda_uretilir()
    {
        var ev = Timed("2024-02-29 09:00", "2024-02-29 10:00", "FREQ=YEARLY");

        var result = Expand(ev, "2024-01-01 00:00", "2033-01-01 00:00");

        Assert.Equal(["2024-02-29 09:00", "2028-02-29 09:00", "2032-02-29 09:00"], result);
    }

    // ------------------------------------------------------------------
    // Bitiş koşulları
    // ------------------------------------------------------------------

    [Fact]
    public void Count_pencere_ilerideyken_bile_seri_basindan_sayilir()
    {
        // Beş örnek: 2, 3, 4, 5, 6 mart. Pencere 4 marttan başlasa bile
        // sayaç serinin başından işler; 7 mart üretilmemelidir.
        var ev = Timed("2026-03-02 09:00", "2026-03-02 10:00", "FREQ=DAILY;COUNT=5");

        var result = Expand(ev, "2026-03-04 00:00", "2026-03-20 00:00");

        Assert.Equal(["2026-03-04 09:00", "2026-03-05 09:00", "2026-03-06 09:00"], result);
    }

    [Fact]
    public void Until_son_ornegi_dahil_eder()
    {
        var ev = Timed("2026-03-02 09:00", "2026-03-02 10:00", "FREQ=DAILY;UNTIL=20260304T070000Z");

        var result = Expand(ev, "2026-03-01 00:00", "2026-03-20 00:00");

        // UNTIL 4 mart 07:00 UTC = 10:00 İstanbul; o günkü 09:00 örneği hâlâ dahildir.
        Assert.Equal(["2026-03-02 09:00", "2026-03-03 09:00", "2026-03-04 09:00"], result);
    }

    [Fact]
    public void Suresiz_seri_pencereyle_sinirlanir()
    {
        var ev = Timed("2020-01-01 09:00", "2020-01-01 10:00", "FREQ=DAILY");

        var result = Expand(ev, "2026-03-02 00:00", "2026-03-05 00:00");

        Assert.Equal(["2026-03-02 09:00", "2026-03-03 09:00", "2026-03-04 09:00"], result);
    }

    [Fact]
    public void Seri_sonu_hesaplanir_suresizde_null_doner()
    {
        var sonlu = Timed("2026-03-02 09:00", "2026-03-02 10:00", "FREQ=DAILY;COUNT=3");
        var suresiz = Timed("2026-03-02 09:00", "2026-03-02 10:00", "FREQ=DAILY");

        Assert.Equal(Utc("2026-03-04 10:00"), _expander.CalculateSeriesEnd(sonlu));
        Assert.Null(_expander.CalculateSeriesEnd(suresiz));
    }

    // ------------------------------------------------------------------
    // İstisnalar
    // ------------------------------------------------------------------

    [Fact]
    public void Exdate_tek_ornegi_kaldirir()
    {
        var ev = Timed("2026-03-02 09:00", "2026-03-02 10:00", "FREQ=DAILY");
        ev.ExDates = "2026-03-03T09:00:00";

        var result = Expand(ev, "2026-03-02 00:00", "2026-03-05 00:00");

        Assert.Equal(["2026-03-02 09:00", "2026-03-04 09:00"], result);
    }

    [Fact]
    public void Tasinmis_ornek_ozgun_saatinde_degil_yeni_saatinde_gorunur()
    {
        var ev = Timed("2026-03-02 09:00", "2026-03-02 10:00", "FREQ=DAILY");
        var moved = Exception(ev, "2026-03-03 09:00", "2026-03-03 15:00", "2026-03-03 16:00");

        var result = Expand(ev, "2026-03-02 00:00", "2026-03-05 00:00", moved);

        Assert.Equal(["2026-03-02 09:00", "2026-03-03 15:00", "2026-03-04 09:00"], result);
    }

    [Fact]
    public void Pencere_disina_tasinan_ornek_pencerede_gorunmez()
    {
        var ev = Timed("2026-03-02 09:00", "2026-03-02 10:00", "FREQ=DAILY");
        // 3 marttaki örnek 20 marta taşındı.
        var moved = Exception(ev, "2026-03-03 09:00", "2026-03-20 09:00", "2026-03-20 10:00");

        var result = Expand(ev, "2026-03-02 00:00", "2026-03-05 00:00", moved);

        Assert.Equal(["2026-03-02 09:00", "2026-03-04 09:00"], result);
    }

    [Fact]
    public void Pencere_icine_tasinan_ornek_gorunur()
    {
        var ev = Timed("2026-03-02 09:00", "2026-03-02 10:00", "FREQ=DAILY");
        // 20 marttaki örnek 3 marta çekildi; 3 mart günü iki örnek olur.
        var moved = Exception(ev, "2026-03-20 09:00", "2026-03-03 18:00", "2026-03-03 19:00");

        var result = Expand(ev, "2026-03-02 00:00", "2026-03-05 00:00", moved);

        Assert.Equal(
            ["2026-03-02 09:00", "2026-03-03 09:00", "2026-03-03 18:00", "2026-03-04 09:00"],
            result);
    }

    [Fact]
    public void Iptal_edilmis_ornek_hic_gorunmez()
    {
        var ev = Timed("2026-03-02 09:00", "2026-03-02 10:00", "FREQ=DAILY");
        var cancelled = Cancelled(ev, "2026-03-03 09:00");

        var result = Expand(ev, "2026-03-02 00:00", "2026-03-05 00:00", cancelled);

        Assert.Equal(["2026-03-02 09:00", "2026-03-04 09:00"], result);
    }

    [Fact]
    public void Cop_kutusundaki_istisna_gorunmez()
    {
        var ev = Timed("2026-03-02 09:00", "2026-03-02 10:00", "FREQ=DAILY");
        var moved = Exception(ev, "2026-03-03 09:00", "2026-03-03 15:00", "2026-03-03 16:00");
        moved.DeletedAt = DateTimeOffset.UtcNow;

        var result = Expand(ev, "2026-03-02 00:00", "2026-03-05 00:00", moved);

        Assert.Equal(["2026-03-02 09:00", "2026-03-04 09:00"], result);
    }

    // ------------------------------------------------------------------
    // Pencere sınırları
    // ------------------------------------------------------------------

    [Fact]
    public void Pencerenin_basinda_hala_suren_etkinlik_dahil_edilir()
    {
        // 22:00-02:00 arası bir etkinlik; pencere gece yarısında başlıyor.
        var ev = Timed("2026-03-02 22:00", "2026-03-03 02:00", "FREQ=DAILY");

        var result = Expand(ev, "2026-03-03 00:00", "2026-03-04 00:00");

        Assert.Equal(["2026-03-02 22:00", "2026-03-03 22:00"], result);
    }

    [Fact]
    public void Pencerenin_bitisinde_baslayan_etkinlik_dahil_edilmez()
    {
        var ev = Timed("2026-03-03 00:00", "2026-03-03 01:00");

        var result = Expand(ev, "2026-03-02 00:00", "2026-03-03 00:00");

        Assert.Empty(result);
    }

    [Fact]
    public void Bitisi_pencereye_degen_etkinlik_dahil_edilmez()
    {
        // 08:00-09:00 etkinlik, pencere 09:00'da başlıyor: bitiş dışlayıcı olduğu için girmez.
        var ev = Timed("2026-03-02 08:00", "2026-03-02 09:00");

        var result = Expand(ev, "2026-03-02 09:00", "2026-03-03 00:00");

        Assert.Empty(result);
    }

    // ------------------------------------------------------------------
    // Tüm gün ve çok günlü
    // ------------------------------------------------------------------

    [Fact]
    public void Cok_gunlu_tum_gun_etkinligi_araligina_deger()
    {
        var ev = AllDay("2026-03-02", days: 3);

        Assert.Single(_expander.Expand(ev, null, Utc("2026-03-03 00:00"), Utc("2026-03-04 00:00")));
        Assert.Empty(_expander.Expand(ev, null, Utc("2026-03-05 00:00"), Utc("2026-03-06 00:00")));
    }

    [Fact]
    public void Tum_gun_etkinligi_haftalik_tekrarlar()
    {
        var ev = AllDay("2026-03-02", days: 1, rrule: "FREQ=WEEKLY;BYDAY=MO");

        var result = Expand(ev, "2026-03-01 00:00", "2026-03-29 00:00");

        Assert.Equal(
            ["2026-03-02 00:00", "2026-03-09 00:00", "2026-03-16 00:00", "2026-03-23 00:00"],
            result);
    }

    // ------------------------------------------------------------------
    // Sıralama
    // ------------------------------------------------------------------

    [Fact]
    public void Cok_seri_birlestirildiginde_kronolojik_siralanir()
    {
        var sabah = Timed("2026-03-02 09:00", "2026-03-02 10:00", "FREQ=DAILY");
        var oglen = Timed("2026-03-02 12:00", "2026-03-02 13:00", "FREQ=DAILY");

        var result = _expander.ExpandMany(
            [oglen, sabah],
            Array.Empty<Takvim.Core.Domain.Event>().ToLookup(e => e.Id),
            Utc("2026-03-02 00:00"),
            Utc("2026-03-04 00:00"));

        Assert.Equal(
            ["2026-03-02 09:00", "2026-03-02 12:00", "2026-03-03 09:00", "2026-03-03 12:00"],
            result.Starts());
    }
}
