using NodaTime;
using Takvim.Core.Localization;

namespace Takvim.Core.Tests;

public class HolidayTests
{
    private readonly TurkishHolidays _holidays = new();

    [Fact]
    public void Sabit_milli_bayramlar_her_yil_uretilir()
    {
        var names = _holidays.ForYear(2026).Where(h => h.IsDayOff).Select(h => h.Name).ToList();

        Assert.Contains("Yılbaşı", names);
        Assert.Contains("Ulusal Egemenlik ve Çocuk Bayramı", names);
        Assert.Contains("Emek ve Dayanışma Günü", names);
        Assert.Contains("Atatürk'ü Anma, Gençlik ve Spor Bayramı", names);
        Assert.Contains("Demokrasi ve Millî Birlik Günü", names);
        Assert.Contains("Zafer Bayramı", names);
        Assert.Contains("Cumhuriyet Bayramı", names);
    }

    [Fact]
    public void Cumhuriyet_bayrami_arefesi_yarim_gundur()
    {
        var eve = _holidays.On(new LocalDate(2026, 10, 28)).Single();

        Assert.Equal(HolidayKind.HalfDayEve, eve.Kind);
        Assert.Equal(new LocalTime(13, 0), eve.WorkEndsAt);
        Assert.False(eve.IsDayOff);
    }

    [Fact]
    public void Ramazan_bayrami_uc_gun_surer_ve_arefesi_vardir()
    {
        // 2026: 20-22 Mart. Arefe 19 Mart.
        Assert.True(_holidays.IsDayOff(new LocalDate(2026, 3, 20)));
        Assert.True(_holidays.IsDayOff(new LocalDate(2026, 3, 21)));
        Assert.True(_holidays.IsDayOff(new LocalDate(2026, 3, 22)));
        Assert.False(_holidays.IsDayOff(new LocalDate(2026, 3, 23)));

        Assert.Equal(new LocalTime(13, 0), _holidays.HalfDayEndsAt(new LocalDate(2026, 3, 19)));
    }

    [Fact]
    public void Kurban_bayrami_dort_gun_surer()
    {
        // 2026: 27-30 Mayıs.
        Assert.True(_holidays.IsDayOff(new LocalDate(2026, 5, 27)));
        Assert.True(_holidays.IsDayOff(new LocalDate(2026, 5, 30)));
        Assert.False(_holidays.IsDayOff(new LocalDate(2026, 5, 31)));
        Assert.Equal(new LocalTime(13, 0), _holidays.HalfDayEndsAt(new LocalDate(2026, 5, 26)));
    }

    [Fact]
    public void Dogrulanmamis_yillar_tahmini_isaretlenir()
    {
        var dogrulanmis = _holidays.On(new LocalDate(2026, 3, 20)).Single();
        var tahmini = _holidays.On(new LocalDate(2029, 2, 14)).Single();

        Assert.False(dogrulanmis.IsEstimated);
        Assert.True(tahmini.IsEstimated);
    }

    [Fact]
    public void Yil_sinirini_asan_bayram_dogru_yila_yazilir()
    {
        // 2032 Ramazan Bayramı 14 Ocak'tadır; arefesi 13 Ocak 2032'dir.
        Assert.True(_holidays.IsDayOff(new LocalDate(2032, 1, 14)));
        Assert.Equal(new LocalTime(13, 0), _holidays.HalfDayEndsAt(new LocalDate(2032, 1, 13)));
    }

    [Fact]
    public void Tablo_disindaki_yilda_yalnizca_sabit_tatiller_kalir()
    {
        var holidays = _holidays.ForYear(2050);

        Assert.Contains(holidays, h => h.Name == "Cumhuriyet Bayramı");
        Assert.DoesNotContain(holidays, h => h.Name.Contains("Ramazan", StringComparison.Ordinal));
        Assert.Equal(2032, _holidays.LastKnownReligiousYear);
    }

    [Fact]
    public void Ayni_tatil_iki_kez_eklenmez()
    {
        // Komşu yıllar tarandığı için bayramın mükerrer eklenmediği doğrulanır.
        var holidays = _holidays.ForYear(2026);
        var duplicates = holidays.GroupBy(h => (h.Date, h.Name)).Where(g => g.Count() > 1).ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Aralik_sorgusu_yil_sinirini_asabilir()
    {
        var range = _holidays.InRange(new LocalDate(2025, 12, 25), new LocalDate(2026, 1, 5)).ToList();

        Assert.Single(range);
        Assert.Equal("Yılbaşı", range[0].Name);
        Assert.Equal(new LocalDate(2026, 1, 1), range[0].Date);
    }
}
