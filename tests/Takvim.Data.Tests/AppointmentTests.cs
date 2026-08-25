using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// Randevu sayfaları. Dilim üretimi iki kaynağı birleştirdiği için sınamalar da
/// iki yönlü: haftalık pencerelerin doğru bölünmesi ve sahibin gerçek
/// meşguliyetinin bu pencerelerden düşülmesi.
/// </summary>
public class AppointmentTests : IDisposable
{
    private readonly TestDatabase _t = new();
    private readonly AppointmentService _appointments;

    public AppointmentTests()
        => _appointments = new AppointmentService(_t.Db, _t.Query, _t.Zones, _t.Events, _t.Clock);

    public void Dispose()
    {
        _t.Dispose();
        GC.SuppressFinalize(this);
    }

    // Sınama saati 2026-01-01 09:00 UTC. 2026-03-02 pazartesidir.
    private static readonly LocalDate Monday = new(2026, 3, 2);

    private Task<AppointmentSchedule> CreateScheduleAsync(
        int duration = 30,
        int buffer = 0,
        int notice = 0,
        int advance = 365,
        int? perDay = null,
        IReadOnlyList<(IsoDayOfWeek, LocalTime, LocalTime)>? windows = null)
        => _appointments.CreateAsync(new AppointmentScheduleInput
        {
            Name = "Görüşme",
            CalendarId = _t.CalendarId,
            DurationMinutes = duration,
            BufferMinutes = buffer,
            MinimumNoticeHours = notice,
            MaximumAdvanceDays = advance,
            MaximumPerDay = perDay,
            Windows = windows ?? [(IsoDayOfWeek.Monday, new LocalTime(9, 0), new LocalTime(11, 0))],
        }, _t.UserId);

    private Task<List<AppointmentSlot>> SlotsAsync(Guid scheduleId, LocalDate? date = null)
    {
        var day = date ?? Monday;
        return _appointments.GetSlotsAsync(scheduleId, day, day.PlusDays(1));
    }

    // ==================================================================
    // Sayfa tanımı
    // ==================================================================

    [Fact]
    public async Task Sayfa_olusturulur_ve_kisa_ad_uretilir()
    {
        var schedule = await CreateScheduleAsync();

        Assert.Equal("gorusme", schedule.Slug);
    }

    [Fact]
    public async Task Ayni_addan_ikinci_sayfa_farkli_kisa_ad_alir()
    {
        await CreateScheduleAsync();
        _t.Detach();

        var second = await CreateScheduleAsync();

        Assert.Equal("gorusme-2", second.Slug);
    }

    [Fact]
    public async Task Bos_ad_reddedilir()
        => await Assert.ThrowsAsync<ArgumentException>(() => _appointments.CreateAsync(
            new AppointmentScheduleInput { Name = "  ", CalendarId = _t.CalendarId }, _t.UserId));

    [Fact]
    public async Task Kisa_addan_sayfa_bulunur()
    {
        var schedule = await CreateScheduleAsync();
        _t.Detach();

        var found = await _appointments.FindBySlugAsync(schedule.Slug);

        Assert.Equal(schedule.Id, found!.Id);
    }

    [Fact]
    public async Task Kapali_sayfa_kisa_addan_bulunmaz()
    {
        var schedule = await CreateScheduleAsync();
        _t.Detach();

        await _appointments.SetActiveAsync(schedule.Id, false);
        _t.Detach();

        Assert.Null(await _appointments.FindBySlugAsync(schedule.Slug));
    }

    [Fact]
    public async Task Ters_pencere_atlanir()
    {
        // 17:00-09:00 bir pencere değildir; sessizce düşer.
        var schedule = await CreateScheduleAsync(
            windows: [(IsoDayOfWeek.Monday, new LocalTime(17, 0), new LocalTime(9, 0))]);

        Assert.Empty(schedule.Windows);
        Assert.Empty(await SlotsAsync(schedule.Id));
    }

    [Fact]
    public async Task Guncelleme_pencereleri_tumuyle_yeniler()
    {
        var schedule = await CreateScheduleAsync();
        _t.Detach();

        await _appointments.UpdateAsync(schedule.Id, new AppointmentScheduleInput
        {
            Name = "Görüşme",
            CalendarId = _t.CalendarId,
            Windows = [(IsoDayOfWeek.Tuesday, new LocalTime(14, 0), new LocalTime(15, 0))],
        });

        _t.Detach();

        var reloaded = await _appointments.FindAsync(schedule.Id);

        Assert.Equal(IsoDayOfWeek.Tuesday, Assert.Single(reloaded!.Windows).DayOfWeek);
        Assert.Equal("Görüşme", reloaded.Name);
    }

    // ==================================================================
    // Dilim üretimi
    // ==================================================================

    [Fact]
    public async Task Pencere_dilimlere_bolunur()
    {
        // 09:00-11:00 arası 30 dakikalık dilimler: 4 tane.
        var schedule = await CreateScheduleAsync();
        _t.Detach();

        var slots = await SlotsAsync(schedule.Id);

        Assert.Equal(4, slots.Count);
        Assert.Equal(Monday.At(new LocalTime(9, 0)), slots[0].StartLocal);
        Assert.Equal(Monday.At(new LocalTime(10, 30)), slots[3].StartLocal);
    }

    [Fact]
    public async Task Tampon_dilim_arasini_acar()
    {
        // 30 + 15 tampon: 09:00, 09:45, 10:15 sığmaz (10:45 > 11:00).
        var schedule = await CreateScheduleAsync(duration: 30, buffer: 15);
        _t.Detach();

        var slots = await SlotsAsync(schedule.Id);

        Assert.Equal([
            Monday.At(new LocalTime(9, 0)),
            Monday.At(new LocalTime(9, 45)),
            Monday.At(new LocalTime(10, 30)),
        ], slots.Select(s => s.StartLocal));
    }

    [Fact]
    public async Task Pencereye_sigmayan_dilim_uretilmez()
    {
        // 09:00-11:00 arasına 45 dakikalık iki dilim sığar, üçüncüsü sığmaz.
        var schedule = await CreateScheduleAsync(duration: 45);
        _t.Detach();

        Assert.Equal(2, (await SlotsAsync(schedule.Id)).Count);
    }

    [Fact]
    public async Task Baska_gunun_penceresi_karismaz()
    {
        var schedule = await CreateScheduleAsync();
        _t.Detach();

        // Salı günü pencere yok.
        Assert.Empty(await SlotsAsync(schedule.Id, Monday.PlusDays(1)));
    }

    [Fact]
    public async Task Iki_pencere_ayri_ayri_bolunur()
    {
        var schedule = await CreateScheduleAsync(windows:
        [
            (IsoDayOfWeek.Monday, new LocalTime(9, 0), new LocalTime(10, 0)),
            (IsoDayOfWeek.Monday, new LocalTime(14, 0), new LocalTime(15, 0)),
        ]);

        _t.Detach();

        var slots = await SlotsAsync(schedule.Id);

        Assert.Equal(4, slots.Count);
        Assert.Equal(Monday.At(new LocalTime(14, 0)), slots[2].StartLocal);
    }

    [Fact]
    public async Task Penceresiz_sayfa_dilim_uretmez()
    {
        var schedule = await CreateScheduleAsync(windows: []);
        _t.Detach();

        Assert.Empty(await SlotsAsync(schedule.Id));
    }

    // ==================================================================
    // Meşguliyet
    // ==================================================================

    [Fact]
    public async Task Mesgul_saat_dilimden_dusulur()
    {
        var schedule = await CreateScheduleAsync();
        _t.Detach();

        await _t.Events.CreateAsync(_t.Input("2026-03-02 09:30", "2026-03-02 10:00", "Başka toplantı"));
        _t.Detach();

        var slots = await SlotsAsync(schedule.Id);

        Assert.DoesNotContain(slots, s => s.StartLocal == Monday.At(new LocalTime(9, 30)));
        Assert.Equal(3, slots.Count);
    }

    [Fact]
    public async Task Kismen_ortusen_toplanti_da_dilimi_kapatir()
    {
        var schedule = await CreateScheduleAsync();
        _t.Detach();

        // 09:15-09:45 iki dilime birden değer.
        await _t.Events.CreateAsync(_t.Input("2026-03-02 09:15", "2026-03-02 09:45"));
        _t.Detach();

        var slots = await SlotsAsync(schedule.Id);

        Assert.Equal(2, slots.Count);
        Assert.Equal(Monday.At(new LocalTime(10, 0)), slots[0].StartLocal);
    }

    [Fact]
    public async Task Musait_isaretli_etkinlik_dilimi_kapatmaz()
    {
        var schedule = await CreateScheduleAsync();
        _t.Detach();

        var input = _t.Input("2026-03-02 09:30", "2026-03-02 10:00") with
        {
            Availability = Availability.Free,
        };

        await _t.Events.CreateAsync(input);
        _t.Detach();

        Assert.Equal(4, (await SlotsAsync(schedule.Id)).Count);
    }

    [Fact]
    public async Task Bitisik_toplanti_dilimi_kapatmaz()
    {
        var schedule = await CreateScheduleAsync();
        _t.Detach();

        // 08:30-09:00 ile 09:00 dilimi çakışmaz.
        await _t.Events.CreateAsync(_t.Input("2026-03-02 08:30", "2026-03-02 09:00"));
        _t.Detach();

        Assert.Equal(4, (await SlotsAsync(schedule.Id)).Count);
    }

    [Fact]
    public async Task Baska_takvimdeki_toplanti_da_sayilir()
    {
        // Sayfanın takvimi değil sahibinin tümü okunur.
        var schedule = await CreateScheduleAsync();
        _t.Detach();

        var other = new Calendar { Name = "İkinci", OwnerUserId = _t.UserId };
        _t.Db.Calendars.Add(other);
        await _t.Db.SaveChangesAsync();
        _t.Detach();

        var input = _t.Input("2026-03-02 09:00", "2026-03-02 09:30") with { CalendarId = other.Id };
        await _t.Events.CreateAsync(input);
        _t.Detach();

        Assert.Equal(3, (await SlotsAsync(schedule.Id)).Count);
    }

    // ==================================================================
    // Sınırlar
    // ==================================================================

    [Fact]
    public async Task Cok_yakin_dilim_gosterilmez()
    {
        // Sınama saati 2026-01-01 09:00 UTC; aynı gün 12:00'ye 48 saat kala yoktur.
        var schedule = await CreateScheduleAsync(
            notice: 48,
            windows: [(IsoDayOfWeek.Thursday, new LocalTime(12, 0), new LocalTime(14, 0))]);

        _t.Detach();

        // 1 Ocak 2026 perşembe.
        Assert.Empty(await SlotsAsync(schedule.Id, new LocalDate(2026, 1, 1)));
    }

    [Fact]
    public async Task Cok_uzak_gun_gosterilmez()
    {
        var schedule = await CreateScheduleAsync(advance: 7);
        _t.Detach();

        // Mart ayı, 7 günlük pencerenin çok ötesinde.
        Assert.Empty(await SlotsAsync(schedule.Id));
    }

    [Fact]
    public async Task Gunluk_sinir_uygulanir()
    {
        var schedule = await CreateScheduleAsync(perDay: 2);
        _t.Detach();

        Assert.Equal(2, (await SlotsAsync(schedule.Id)).Count);
    }

    [Fact]
    public async Task Alinan_randevu_gunluk_sinirdan_dusulur()
    {
        var schedule = await CreateScheduleAsync(perDay: 2);
        _t.Detach();

        var slots = await SlotsAsync(schedule.Id);
        await _appointments.BookAsync(schedule.Id, slots[0].StartUtc, "Ayşe");
        _t.Detach();

        // Biri alındı, sınır 2; geriye bir dilim kalır.
        Assert.Single(await SlotsAsync(schedule.Id));
    }

    // ==================================================================
    // Randevu alma
    // ==================================================================

    [Fact]
    public async Task Randevu_alinir_ve_takvime_yazilir()
    {
        var schedule = await CreateScheduleAsync();
        _t.Detach();

        var slots = await SlotsAsync(schedule.Id);
        var result = await _appointments.BookAsync(
            schedule.Id, slots[0].StartUtc, "Ayşe Yılmaz", "ayse@ornek.local", "Bütçe konuşacağız");

        _t.Detach();

        Assert.True(result.Success);

        var created = await _t.Db.Events.AsNoTracking()
            .FirstAsync(e => e.Id == result.Appointment!.EventId);

        Assert.Equal("Görüşme — Ayşe Yılmaz", created.Title);
        Assert.Contains("Bütçe konuşacağız", created.DescriptionHtml, StringComparison.Ordinal);
        Assert.Equal(Monday.At(new LocalTime(9, 30)), created.EndLocal);
    }

    [Fact]
    public async Task Alinan_saat_dilimlerden_cikar()
    {
        var schedule = await CreateScheduleAsync();
        _t.Detach();

        var slots = await SlotsAsync(schedule.Id);
        await _appointments.BookAsync(schedule.Id, slots[0].StartUtc, "Ayşe");
        _t.Detach();

        var remaining = await SlotsAsync(schedule.Id);

        Assert.Equal(3, remaining.Count);
        Assert.DoesNotContain(remaining, s => s.StartUtc == slots[0].StartUtc);
    }

    [Fact]
    public async Task Kapilan_saat_ikinci_kez_verilmez()
    {
        var schedule = await CreateScheduleAsync();
        _t.Detach();

        var slots = await SlotsAsync(schedule.Id);

        await _appointments.BookAsync(schedule.Id, slots[0].StartUtc, "Ayşe");
        _t.Detach();

        // Listeyi göstermek söz vermek değildir.
        var second = await _appointments.BookAsync(schedule.Id, slots[0].StartUtc, "Ali");

        Assert.False(second.Success);
        Assert.Contains("uygun değil", second.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pencere_disina_randevu_alinamaz()
    {
        var schedule = await CreateScheduleAsync();
        _t.Detach();

        var outside = _t.Utc("2026-03-02 15:00");

        Assert.False((await _appointments.BookAsync(schedule.Id, outside, "Ayşe")).Success);
    }

    [Fact]
    public async Task Kapali_sayfaya_randevu_alinamaz()
    {
        var schedule = await CreateScheduleAsync();
        _t.Detach();

        var slots = await SlotsAsync(schedule.Id);

        await _appointments.SetActiveAsync(schedule.Id, false);
        _t.Detach();

        Assert.False((await _appointments.BookAsync(schedule.Id, slots[0].StartUtc, "Ayşe")).Success);
    }

    [Fact]
    public async Task Bos_ad_ile_randevu_alinamaz()
    {
        var schedule = await CreateScheduleAsync();
        _t.Detach();

        var slots = await SlotsAsync(schedule.Id);

        await Assert.ThrowsAsync<ArgumentException>(
            () => _appointments.BookAsync(schedule.Id, slots[0].StartUtc, "   "));
    }

    [Fact]
    public async Task Cevrimici_sayfada_her_randevu_kendi_odasini_alir()
    {
        var schedule = await _appointments.CreateAsync(new AppointmentScheduleInput
        {
            Name = "Uzaktan görüşme",
            CalendarId = _t.CalendarId,
            OnlineMeetingProvider = "jitsi",
            BufferMinutes = 0,
            MinimumNoticeHours = 0,
            MaximumAdvanceDays = 365,
            Windows = [(IsoDayOfWeek.Monday, new LocalTime(9, 0), new LocalTime(11, 0))],
        }, _t.UserId);

        _t.Detach();

        var slots = await SlotsAsync(schedule.Id);

        var first = await _appointments.BookAsync(schedule.Id, slots[0].StartUtc, "Ayşe");
        _t.Detach();
        var second = await _appointments.BookAsync(schedule.Id, slots[1].StartUtc, "Ali");
        _t.Detach();

        var urls = await _t.Db.Events.AsNoTracking()
            .Where(e => e.Id == first.Appointment!.EventId || e.Id == second.Appointment!.EventId)
            .Select(e => e.OnlineMeetingUrl)
            .ToListAsync();

        Assert.All(urls, url => Assert.StartsWith("https://meet.jit.si/", url, StringComparison.Ordinal));
        Assert.Equal(2, urls.Distinct().Count());
    }

    // ==================================================================
    // İptal
    // ==================================================================

    [Fact]
    public async Task Iptal_saati_serbest_birakir()
    {
        var schedule = await CreateScheduleAsync();
        _t.Detach();

        var slots = await SlotsAsync(schedule.Id);
        var booked = await _appointments.BookAsync(schedule.Id, slots[0].StartUtc, "Ayşe");
        _t.Detach();

        await _appointments.CancelAsync(booked.Appointment!.Id, "Vazgeçildi", _t.UserId);
        _t.Detach();

        Assert.Equal(4, (await SlotsAsync(schedule.Id)).Count);
    }

    [Fact]
    public async Task Iptal_edilen_randevu_listede_gorunmez()
    {
        var schedule = await CreateScheduleAsync();
        _t.Detach();

        var slots = await SlotsAsync(schedule.Id);
        var booked = await _appointments.BookAsync(schedule.Id, slots[0].StartUtc, "Ayşe");
        _t.Detach();

        await _appointments.CancelAsync(booked.Appointment!.Id, null, _t.UserId);
        _t.Detach();

        Assert.Empty(await _appointments.GetAppointmentsAsync(schedule.Id));
        Assert.Single(await _appointments.GetAppointmentsAsync(schedule.Id, includeCancelled: true));
    }

    [Fact]
    public async Task Iptal_iki_kez_calistirilabilir()
    {
        var schedule = await CreateScheduleAsync();
        _t.Detach();

        var slots = await SlotsAsync(schedule.Id);
        var booked = await _appointments.BookAsync(schedule.Id, slots[0].StartUtc, "Ayşe");
        _t.Detach();

        await _appointments.CancelAsync(booked.Appointment!.Id, null, _t.UserId);
        _t.Detach();
        await _appointments.CancelAsync(booked.Appointment.Id, null, _t.UserId);
    }

    [Fact]
    public async Task Yaklasan_randevular_listelenir()
    {
        var schedule = await CreateScheduleAsync();
        _t.Detach();

        var slots = await SlotsAsync(schedule.Id);
        await _appointments.BookAsync(schedule.Id, slots[0].StartUtc, "Ayşe");
        _t.Detach();

        Assert.Single(await _appointments.GetUpcomingAsync(_t.UserId));
    }

    [Fact]
    public async Task Baskasinin_randevulari_gelmez()
    {
        var schedule = await CreateScheduleAsync();
        _t.Detach();

        var slots = await SlotsAsync(schedule.Id);
        await _appointments.BookAsync(schedule.Id, slots[0].StartUtc, "Ayşe");
        _t.Detach();

        var (otherUserId, _) = _t.AddUser("Ali", "ali@ornek.local");

        Assert.Empty(await _appointments.GetUpcomingAsync(otherUserId));
    }
}
