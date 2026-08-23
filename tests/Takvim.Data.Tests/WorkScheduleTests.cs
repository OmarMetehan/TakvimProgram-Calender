using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Localization;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// Çalışma düzeni. Sınanan asıl kural: resmi tatil takvimi haftalık mesai
/// tanımını ezer — arefe günü mesai erken biter, tam gün tatilde çalışılmaz.
/// </summary>
public class WorkScheduleTests : IDisposable
{
    private readonly TestDatabase _t = new();
    private readonly WorkScheduleService _schedule;

    public WorkScheduleTests()
    {
        _schedule = new WorkScheduleService(_t.Db, new TurkishHolidays());

        _t.Db.WorkingHours.AddRange(WorkingHours.DefaultWeek(_t.UserId));
        _t.Db.SaveChanges();
        _t.Detach();
    }

    public void Dispose()
    {
        _t.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<DaySchedule> Day(int year, int month, int day)
        => _schedule.GetDayAsync(_t.UserId, new LocalDate(year, month, day));

    // ------------------------------------------------------------------
    // Haftalık tanım
    // ------------------------------------------------------------------

    [Fact]
    public async Task Varsayilan_hafta_ici_dokuz_alti_calisir()
    {
        var monday = await Day(2026, 3, 2);

        Assert.True(monday.IsWorkingDay);
        Assert.Equal(new LocalTime(9, 0), monday.Start);
        Assert.Equal(new LocalTime(18, 0), monday.End);
    }

    [Fact]
    public async Task Varsayilan_hafta_sonu_calisilmaz()
    {
        Assert.False((await Day(2026, 3, 7)).IsWorkingDay);   // cumartesi
        Assert.False((await Day(2026, 3, 8)).IsWorkingDay);   // pazar
    }

    [Fact]
    public async Task Gun_bazinda_farkli_saat_tanimlanabilir()
    {
        await _schedule.SaveWeeklyAsync(_t.UserId,
        [
            new WorkingHours
            {
                UserId = _t.UserId,
                DayOfWeek = IsoDayOfWeek.Friday,
                IsWorkingDay = true,
                Start = new LocalTime(9, 0),
                End = new LocalTime(13, 0),
            },
        ]);
        _t.Detach();

        var friday = await Day(2026, 3, 6);

        Assert.Equal(new LocalTime(13, 0), friday.End);
        // Diğer günler etkilenmez.
        Assert.Equal(new LocalTime(18, 0), (await Day(2026, 3, 5)).End);
    }

    [Fact]
    public async Task Bir_gun_tumuyle_kapatilabilir()
    {
        await _schedule.SaveWeeklyAsync(_t.UserId,
        [
            new WorkingHours { UserId = _t.UserId, DayOfWeek = IsoDayOfWeek.Wednesday, IsWorkingDay = false },
        ]);
        _t.Detach();

        Assert.False((await Day(2026, 3, 4)).IsWorkingDay);
    }

    // ------------------------------------------------------------------
    // Tatil kuralı mesaiyi ezer
    // ------------------------------------------------------------------

    [Fact]
    public async Task Tam_gun_tatilde_calisilmaz()
    {
        // 23 Nisan 2026 perşembe: Ulusal Egemenlik ve Çocuk Bayramı.
        var holiday = await Day(2026, 4, 23);

        Assert.False(holiday.IsWorkingDay);
        Assert.Equal("Ulusal Egemenlik ve Çocuk Bayramı", holiday.HolidayName);
    }

    [Fact]
    public async Task Arefe_gununde_mesai_erken_biter()
    {
        // 28 Ekim: Cumhuriyet Bayramı arefesi, 13:00'ten sonrası tatil.
        var eve = await Day(2026, 10, 28);

        Assert.True(eve.IsWorkingDay);
        Assert.Equal(new LocalTime(13, 0), eve.End);
        Assert.Contains("arefe", eve.HolidayName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Arefe_kurali_mesai_zaten_erken_bitiyorsa_uzatmaz()
    {
        // Cuma mesaisi 12:00'de bitiyorsa, arefe kuralı onu 13:00'e çekmez.
        await _schedule.SaveWeeklyAsync(_t.UserId,
        [
            new WorkingHours
            {
                UserId = _t.UserId,
                DayOfWeek = IsoDayOfWeek.Wednesday,
                IsWorkingDay = true,
                Start = new LocalTime(9, 0),
                End = new LocalTime(12, 0),
            },
        ]);
        _t.Detach();

        // 28 Ekim 2026 çarşamba.
        Assert.Equal(new LocalTime(12, 0), (await Day(2026, 10, 28)).End);
    }

    // ------------------------------------------------------------------
    // Çalışma konumu
    // ------------------------------------------------------------------

    [Fact]
    public async Task Calisma_konumu_kaydedilir_ve_okunur()
    {
        var date = new LocalDate(2026, 3, 3);
        await _schedule.SetLocationAsync(_t.UserId, date, WorkLocation.Home);
        _t.Detach();

        var day = await Day(2026, 3, 3);

        Assert.Equal(WorkLocation.Home, day.Location);
        Assert.Equal("Evden", day.LocationLabel);
    }

    [Fact]
    public async Task Sube_konumu_serbest_metin_tasir()
    {
        var date = new LocalDate(2026, 3, 3);
        await _schedule.SetLocationAsync(_t.UserId, date, WorkLocation.Branch, "Ankara ofisi");
        _t.Detach();

        Assert.Equal("Ankara ofisi", (await Day(2026, 3, 3)).LocationLabel);
    }

    [Fact]
    public async Task Konum_belirtilmemise_cevrilince_kayit_silinir()
    {
        var date = new LocalDate(2026, 3, 3);
        await _schedule.SetLocationAsync(_t.UserId, date, WorkLocation.Home);
        _t.Detach();

        await _schedule.SetLocationAsync(_t.UserId, date, WorkLocation.Unspecified);
        _t.Detach();

        Assert.Empty(_t.Db.WorkLocations);
        Assert.Equal(WorkLocation.Unspecified, (await Day(2026, 3, 3)).Location);
    }

    [Fact]
    public async Task Calisilmayan_gun_varsayilan_olarak_uzakta_sayilir()
    {
        // Konum kaydı yoksa, çalışılmayan gün "çalışmıyor" gösterilir.
        Assert.Equal(WorkLocation.Away, (await Day(2026, 3, 7)).Location);
    }

    [Fact]
    public async Task Ayni_gune_ikinci_kayit_acilmaz()
    {
        var date = new LocalDate(2026, 3, 3);
        await _schedule.SetLocationAsync(_t.UserId, date, WorkLocation.Home);
        _t.Detach();
        await _schedule.SetLocationAsync(_t.UserId, date, WorkLocation.Office);
        _t.Detach();

        Assert.Single(_t.Db.WorkLocations);
        Assert.Equal(WorkLocation.Office, (await Day(2026, 3, 3)).Location);
    }

    // ------------------------------------------------------------------
    // Aralık sorgusu
    // ------------------------------------------------------------------

    [Fact]
    public async Task Aralik_sorgusu_her_gun_icin_kayit_dondurur()
    {
        var range = await _schedule.GetRangeAsync(
            _t.UserId, new LocalDate(2026, 3, 2), new LocalDate(2026, 3, 9));

        Assert.Equal(7, range.Count);
        Assert.Equal(5, range.Values.Count(d => d.IsWorkingDay));
    }

    [Fact]
    public async Task Oranlar_izgara_icin_dogru_hesaplanir()
    {
        var monday = await Day(2026, 3, 2);

        // 09:00 = günün 3/8'i, 18:00 = 3/4'ü.
        Assert.Equal(0.375, monday.StartRatio, precision: 5);
        Assert.Equal(0.75, monday.EndRatio, precision: 5);
    }
}
