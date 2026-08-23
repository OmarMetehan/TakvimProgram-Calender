using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Localization;
using Takvim.Core.Scheduling;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// Zamanlama yardımcısının veri yolu: etkinliklerden meşguliyet aralıkları
/// çıkarılması ve tekrarlayan serilerin doğru genişletilmesi.
/// </summary>
public class SchedulingTests : IDisposable
{
    private readonly TestDatabase _t = new();
    private readonly SchedulingService _scheduling;

    public SchedulingTests()
    {
        var workSchedule = new WorkScheduleService(_t.Db, new TurkishHolidays());
        _scheduling = new SchedulingService(_t.Db, _t.Expander, workSchedule);

        _t.Db.WorkingHours.AddRange(WorkingHours.DefaultWeek(_t.UserId));
        _t.Db.SaveChanges();
        _t.Detach();
    }

    public void Dispose()
    {
        _t.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<List<ScheduleLane>> Lanes(string day = "2026-03-02")
    {
        var date = TestDatabase.Parse(day + " 00:00").Date;

        return _scheduling.GetLanesAsync(
            [_t.CalendarId],
            _t.Utc(day + " 00:00"),
            _t.Utc(day + " 00:00") + Duration.FromDays(1),
            _t.UserId,
            date,
            "Europe/Istanbul");
    }

    private string Range(BusyInterval busy)
        => $"{_t.Zones.ToLocal(busy.Start, "Europe/Istanbul"):HH:mm}-" +
           $"{_t.Zones.ToLocal(busy.End, "Europe/Istanbul"):HH:mm}";

    // ------------------------------------------------------------------

    [Fact]
    public async Task Bos_takvim_bos_serit_uretir()
    {
        var lanes = await Lanes();

        Assert.Single(lanes);
        Assert.Equal("Kişisel", lanes[0].Name);
        Assert.Empty(lanes[0].Busy);
    }

    [Fact]
    public async Task Etkinlikler_mesguliyet_araligina_donusur()
    {
        await _t.Events.CreateAsync(_t.Input("2026-03-02 10:00", "2026-03-02 11:00"));
        _t.Detach();

        var lanes = await Lanes();

        Assert.Single(lanes[0].Busy);
        Assert.Equal("10:00-11:00", Range(lanes[0].Busy[0]));
    }

    [Fact]
    public async Task Bitisik_etkinlikler_tek_aralikta_birlesir()
    {
        await _t.Events.CreateAsync(_t.Input("2026-03-02 10:00", "2026-03-02 11:00", "İlk"));
        _t.Detach();
        await _t.Events.CreateAsync(_t.Input("2026-03-02 11:00", "2026-03-02 12:00", "İkinci"));
        _t.Detach();

        var lanes = await Lanes();

        Assert.Single(lanes[0].Busy);
        Assert.Equal("10:00-12:00", Range(lanes[0].Busy[0]));
    }

    [Fact]
    public async Task Tekrarlayan_seri_o_gunun_ornegini_uretir()
    {
        await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:15", "2026-03-02 09:30", "Ayakta toplantı", "FREQ=DAILY"));
        _t.Detach();

        // Seri 2 martta başlıyor; 5 martta da bir örneği olmalı.
        var lanes = await Lanes("2026-03-05");

        Assert.Single(lanes[0].Busy);
        Assert.Equal("09:15-09:30", Range(lanes[0].Busy[0]));
    }

    [Fact]
    public async Task Silinen_ornek_mesguliyet_uretmez()
    {
        var created = await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:15", "2026-03-02 09:30", "Ayakta toplantı", "FREQ=DAILY"));
        _t.Detach();

        await _t.Events.DeleteAsync(created.PrimaryEventId, TestDatabase.Parse("2026-03-05 09:15"),
            SeriesEditScope.ThisOnly, _t.UserId);
        _t.Detach();

        Assert.Empty((await Lanes("2026-03-05"))[0].Busy);
    }

    [Fact]
    public async Task Musait_etkinlik_engel_saymaz()
    {
        var input = _t.Input("2026-03-02 10:00", "2026-03-02 11:00") with { Availability = Availability.Free };
        await _t.Events.CreateAsync(input);
        _t.Detach();

        var lanes = await Lanes();

        Assert.Single(lanes[0].Busy);
        Assert.False(lanes[0].Busy[0].IsBlocking);
    }

    [Fact]
    public async Task Cop_kutusundaki_etkinlik_mesguliyet_uretmez()
    {
        var created = await _t.Events.CreateAsync(_t.Input("2026-03-02 10:00", "2026-03-02 11:00"));
        _t.Detach();
        await _t.Events.DeleteAsync(created.PrimaryEventId, null, SeriesEditScope.AllInSeries, _t.UserId);
        _t.Detach();

        Assert.Empty((await Lanes())[0].Busy);
    }

    // ------------------------------------------------------------------
    // Mesai saatleri
    // ------------------------------------------------------------------

    [Fact]
    public async Task Serit_mesai_saatlerini_tasir()
    {
        var lanes = await Lanes("2026-03-02");   // pazartesi

        Assert.NotNull(lanes[0].WorkingHours);
        Assert.Equal(_t.Utc("2026-03-02 09:00"), lanes[0].WorkingHours!.Value.Start);
        Assert.Equal(_t.Utc("2026-03-02 18:00"), lanes[0].WorkingHours!.Value.End);
    }

    [Fact]
    public async Task Calisilmayan_gunde_mesai_araligi_bos_gelir()
    {
        var lanes = await Lanes("2026-03-07");   // cumartesi

        Assert.Null(lanes[0].WorkingHours);
    }

    // ------------------------------------------------------------------
    // Uçtan uca öneri
    // ------------------------------------------------------------------

    [Fact]
    public async Task Dolu_bir_gunde_ilk_bos_aralik_onerilir()
    {
        await _t.Events.CreateAsync(_t.Input("2026-03-02 09:00", "2026-03-02 10:30", "Sabah"));
        _t.Detach();
        await _t.Events.CreateAsync(_t.Input("2026-03-02 11:00", "2026-03-02 12:00", "Öğleden önce"));
        _t.Detach();

        var lanes = await Lanes();

        var suggestions = FreeBusy.Suggest(
            lanes,
            _t.Utc("2026-03-02 09:00"),
            _t.Utc("2026-03-02 18:00"),
            Duration.FromMinutes(30),
            Duration.FromMinutes(30));

        // 10:30-11:00 arası boş ve en erken uygun aralık.
        var best = suggestions[0];
        Assert.True(best.IsPerfect);
        Assert.Equal(_t.Utc("2026-03-02 10:30"), best.Start);
    }

    [Fact]
    public async Task Bos_takvim_listesi_serit_uretmez()
    {
        var lanes = await _scheduling.GetLanesAsync(
            [], _t.Utc("2026-03-02 00:00"), _t.Utc("2026-03-03 00:00"),
            _t.UserId, new LocalDate(2026, 3, 2), "Europe/Istanbul");

        Assert.Empty(lanes);
    }

    // ------------------------------------------------------------------
    // Kişi satırları
    // ------------------------------------------------------------------

    [Fact]
    public async Task Kisi_satiri_kullanicinin_tum_takvimlerini_kapsar()
    {
        // Kişinin iş ve kişisel takvimi ayrı; müsaitlik ikisinin birleşimidir.
        var ikinciTakvim = new Calendar { Name = "İş", OwnerUserId = _t.UserId };
        _t.Db.Calendars.Add(ikinciTakvim);
        _t.Db.SaveChanges();
        _t.Detach();

        await _t.Events.CreateAsync(_t.Input("2026-03-02 10:00", "2026-03-02 11:00", "Kişisel"));
        _t.Detach();

        var isEtkinligi = _t.Input("2026-03-02 14:00", "2026-03-02 15:00", "İş") with
        {
            CalendarId = ikinciTakvim.Id,
        };
        await _t.Events.CreateAsync(isEtkinligi);
        _t.Detach();

        var lanes = await _scheduling.GetPeopleLanesAsync(
            [(_t.UserId, "Ben", true)],
            _t.Utc("2026-03-02 00:00"),
            _t.Utc("2026-03-03 00:00"),
            new LocalDate(2026, 3, 2),
            "Europe/Istanbul");

        Assert.Single(lanes);
        Assert.Equal(2, lanes[0].Busy.Count);
    }

    [Fact]
    public async Task Kisi_satirinda_zorunluluk_tasinir()
    {
        var (ayse, _) = _t.AddUser("Ayşe", "ayse@ornek.local");

        var lanes = await _scheduling.GetPeopleLanesAsync(
            [(_t.UserId, "Ben", true), (ayse, "Ayşe", false)],
            _t.Utc("2026-03-02 00:00"),
            _t.Utc("2026-03-03 00:00"),
            new LocalDate(2026, 3, 2),
            "Europe/Istanbul");

        Assert.True(lanes[0].IsRequired);
        Assert.False(lanes[1].IsRequired);
    }

    [Fact]
    public async Task Kisi_satiri_baskasinin_etkinlik_basligini_tasimaz()
    {
        // Serbest/meşgul paylaşımının tanımı: yalnızca aralık, içerik değil.
        var (ayse, ayseCal) = _t.AddUser("Ayşe", "ayse@ornek.local");

        var gizli = _t.Input("2026-03-02 10:00", "2026-03-02 11:00", "Gizli görüşme") with
        {
            CalendarId = ayseCal,
        };
        await _t.Events.CreateAsync(gizli);
        _t.Detach();

        var lanes = await _scheduling.GetPeopleLanesAsync(
            [(ayse, "Ayşe", true)],
            _t.Utc("2026-03-02 00:00"),
            _t.Utc("2026-03-03 00:00"),
            new LocalDate(2026, 3, 2),
            "Europe/Istanbul");

        // Satırda yalnızca meşgul aralığı var; başlık taşıyan bir alan yok.
        Assert.Single(lanes[0].Busy);
        Assert.Equal("Ayşe", lanes[0].Name);
    }

    [Fact]
    public async Task Bos_katilimci_listesi_satir_uretmez()
        => Assert.Empty(await _scheduling.GetPeopleLanesAsync(
            [], _t.Utc("2026-03-02 00:00"), _t.Utc("2026-03-03 00:00"),
            new LocalDate(2026, 3, 2), "Europe/Istanbul"));
}
