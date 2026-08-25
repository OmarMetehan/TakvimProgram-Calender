using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Permissions;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// Takvim yönetimi. İki şey ayrıca sınanır: silinen takvimin etkinliklerinin
/// birlikte gidip birlikte dönmesi, ve başka servislerin yönettiği takvimlere
/// (kaynak, abone, tatil) buradan dokunulamaması.
/// </summary>
public class CalendarManagementTests : IDisposable
{
    private readonly TestDatabase _t = new();
    private readonly CalendarService _calendars;

    public CalendarManagementTests()
        => _calendars = new CalendarService(_t.Db, _t.Zones, _t.Clock);

    public void Dispose()
    {
        _t.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<Calendar> CreateAsync(string name = "Proje", string color = "basil")
        => _calendars.CreateAsync(_t.UserId, new CalendarInput { Name = name, Color = color });

    /// <summary>Başka bir servisin yönettiği bir takvim kurar.</summary>
    private async Task<Guid> CreateManagedAsync(CalendarKind kind)
    {
        var calendar = new Calendar { Name = "Yönetilen", OwnerUserId = _t.UserId, Kind = kind };

        _t.Db.Calendars.Add(calendar);
        await _t.Db.SaveChangesAsync();
        _t.Detach();

        return calendar.Id;
    }

    private Task<int> LiveEventCountAsync(Guid calendarId)
        => _t.Db.Events.CountAsync(e => e.CalendarId == calendarId && e.DeletedAt == null);

    // ==================================================================
    // Oluşturma ve düzenleme
    // ==================================================================

    [Fact]
    public async Task Takvim_olusturulur()
    {
        var calendar = await CreateAsync();
        _t.Detach();

        Assert.Equal("Proje", calendar.Name);
        Assert.Equal("basil", calendar.Color);
        Assert.False(calendar.IsReadOnly);

        // Kişisel takvim ve yenisi.
        Assert.Equal(2, (await _calendars.GetOwnAsync(_t.UserId)).Count);
    }

    [Fact]
    public async Task Addaki_bosluklar_kirpilir()
        => Assert.Equal("Proje", (await CreateAsync("  Proje  ")).Name);

    [Fact]
    public async Task Bos_ad_reddedilir()
        => await Assert.ThrowsAsync<ArgumentException>(() => CreateAsync("   "));

    [Fact]
    public async Task Takvim_sayisi_sinirlanir()
    {
        // Kişisel takvim zaten var; sınıra kadar doldurulur.
        for (var i = 1; i < CalendarService.MaxPerUser; i++)
        {
            await CreateAsync($"Takvim {i}");
            _t.Detach();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateAsync("Fazlalık"));
    }

    [Fact]
    public async Task Takvim_guncellenir()
    {
        var calendar = await CreateAsync();
        _t.Detach();

        var problem = await _calendars.UpdateAsync(calendar.Id, new CalendarInput
        {
            Name = "Yeni ad",
            Color = "grape",
            Description = "Açıklama",
            DefaultReminderMinutes = 30,
            DefaultAllDayReminderMinutes = null,
            TimeZoneId = "Europe/London",
        });

        _t.Detach();

        Assert.Null(problem);

        var reloaded = await _t.Db.Calendars.AsNoTracking().FirstAsync(c => c.Id == calendar.Id);

        Assert.Equal("Yeni ad", reloaded.Name);
        Assert.Equal("grape", reloaded.Color);
        Assert.Equal(30, reloaded.DefaultReminderMinutes);
        Assert.Null(reloaded.DefaultAllDayReminderMinutes);
        Assert.Equal("Europe/London", reloaded.TimeZoneId);
    }

    [Fact]
    public async Task Bilinmeyen_zaman_dilimi_varsayilana_duser()
    {
        // Bilinmeyen bir dilim etkinliklerin saatini bozardı.
        var calendar = await _calendars.CreateAsync(
            _t.UserId, new CalendarInput { Name = "Proje", TimeZoneId = "Mars/Olympus" });

        Assert.Equal(Takvim.Core.Time.TimeZoneService.DefaultZoneId, calendar.TimeZoneId);
    }

    [Fact]
    public async Task Renk_tek_basina_degistirilebilir()
    {
        var calendar = await CreateAsync();
        _t.Detach();

        await _calendars.SetColorAsync(calendar.Id, "lavender");
        _t.Detach();

        var reloaded = await _t.Db.Calendars.AsNoTracking().FirstAsync(c => c.Id == calendar.Id);

        Assert.Equal("lavender", reloaded.Color);
        Assert.Equal("Proje", reloaded.Name);
    }

    [Fact]
    public async Task Takvimler_yeniden_siralanir()
    {
        var first = await CreateAsync("Bir");
        _t.Detach();
        var second = await CreateAsync("İki");
        _t.Detach();

        await _calendars.ReorderAsync(_t.UserId, [second.Id, first.Id, _t.CalendarId]);
        _t.Detach();

        var order = (await _calendars.GetOwnAsync(_t.UserId)).Select(c => c.Name).ToList();

        Assert.Equal(["İki", "Bir", "Kişisel"], order);
    }

    [Fact]
    public async Task Olmayan_takvim_guncellenemez()
        => Assert.NotNull(await _calendars.UpdateAsync(
            Guid.NewGuid(), new CalendarInput { Name = "x" }));

    // ==================================================================
    // Başka servislerin takvimleri
    // ==================================================================

    [Theory]
    [InlineData(CalendarKind.Resource)]
    [InlineData(CalendarKind.Subscribed)]
    [InlineData(CalendarKind.Holiday)]
    public async Task Baska_servisin_takvimi_duzenlenemez(CalendarKind kind)
    {
        var calendarId = await CreateManagedAsync(kind);

        var problem = await _calendars.UpdateAsync(calendarId, new CalendarInput { Name = "Yeni" });

        Assert.NotNull(problem);
        _t.Detach();

        // Adı değişmemiş olmalı.
        var reloaded = await _t.Db.Calendars.AsNoTracking().FirstAsync(c => c.Id == calendarId);
        Assert.Equal("Yönetilen", reloaded.Name);
    }

    [Theory]
    [InlineData(CalendarKind.Resource)]
    [InlineData(CalendarKind.Subscribed)]
    [InlineData(CalendarKind.Holiday)]
    public async Task Baska_servisin_takvimi_silinemez(CalendarKind kind)
    {
        var calendarId = await CreateManagedAsync(kind);

        Assert.NotNull(await _calendars.DeleteAsync(calendarId));
        _t.Detach();

        var reloaded = await _t.Db.Calendars.AsNoTracking().FirstAsync(c => c.Id == calendarId);
        Assert.Null(reloaded.DeletedAt);
    }

    [Fact]
    public async Task Ekip_takvimi_duzenlenebilir()
    {
        // Yalnızca kaynak, abone ve tatil takvimleri korunur.
        var calendarId = await CreateManagedAsync(CalendarKind.Team);

        Assert.Null(await _calendars.UpdateAsync(calendarId, new CalendarInput { Name = "Ekip" }));
    }

    // ==================================================================
    // Silme ve geri alma
    // ==================================================================

    [Fact]
    public async Task Silinen_takvim_listede_gorunmez()
    {
        var calendar = await CreateAsync();
        _t.Detach();

        Assert.Null(await _calendars.DeleteAsync(calendar.Id));
        _t.Detach();

        Assert.DoesNotContain(await _calendars.GetOwnAsync(_t.UserId), c => c.Id == calendar.Id);
        Assert.Single(await _calendars.GetDeletedAsync(_t.UserId));
    }

    [Fact]
    public async Task Takvimle_birlikte_etkinlikleri_de_gider()
    {
        var calendar = await CreateAsync();
        _t.Detach();

        var input = _t.Input("2026-03-02 10:00", "2026-03-02 11:00") with { CalendarId = calendar.Id };
        await _t.Events.CreateAsync(input);
        _t.Detach();

        await _calendars.DeleteAsync(calendar.Id);
        _t.Detach();

        Assert.Equal(0, await LiveEventCountAsync(calendar.Id));
    }

    [Fact]
    public async Task Geri_alinan_takvimin_etkinlikleri_de_doner()
    {
        var calendar = await CreateAsync();
        _t.Detach();

        var input = _t.Input("2026-03-02 10:00", "2026-03-02 11:00") with { CalendarId = calendar.Id };
        await _t.Events.CreateAsync(input);
        _t.Detach();

        await _calendars.DeleteAsync(calendar.Id);
        _t.Detach();
        await _calendars.RestoreAsync(calendar.Id);
        _t.Detach();

        Assert.Contains(await _calendars.GetOwnAsync(_t.UserId), c => c.Id == calendar.Id);
        Assert.Equal(1, await LiveEventCountAsync(calendar.Id));
    }

    [Fact]
    public async Task Once_silinmis_etkinlik_geri_gelmez()
    {
        var calendar = await CreateAsync();
        _t.Detach();

        var input = _t.Input("2026-03-02 10:00", "2026-03-02 11:00") with { CalendarId = calendar.Id };
        var created = await _t.Events.CreateAsync(input);
        _t.Detach();

        // Kullanıcı etkinliği tek tek sildi.
        await _t.Events.DeleteAsync(created.PrimaryEventId, null, SeriesEditScope.AllInSeries, _t.UserId);
        _t.Detach();

        // Sonra takvimi sildi ve geri aldı.
        _t.Clock.Advance(Duration.FromMinutes(5));
        await _calendars.DeleteAsync(calendar.Id);
        _t.Detach();
        await _calendars.RestoreAsync(calendar.Id);
        _t.Detach();

        // Takvim döndü ama tek tek silinen etkinlik silinmiş kalmalı.
        Assert.Equal(0, await LiveEventCountAsync(calendar.Id));
    }

    [Fact]
    public async Task Son_kisisel_takvim_silinemez()
    {
        // Hesabın kendisine aittir; silinirse etkinlik ekleyecek yer kalmaz.
        var problem = await _calendars.DeleteAsync(_t.CalendarId);

        Assert.NotNull(problem);
        Assert.Contains("kişisel", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ikinci_kisisel_takvim_varken_silinebilir()
    {
        var second = new Calendar
        {
            Name = "İkinci kişisel",
            OwnerUserId = _t.UserId,
            Kind = CalendarKind.Personal,
        };

        _t.Db.Calendars.Add(second);
        await _t.Db.SaveChangesAsync();
        _t.Detach();

        Assert.Null(await _calendars.DeleteAsync(second.Id));
    }

    [Fact]
    public async Task Suresi_dolan_takvim_kalici_silinir()
    {
        var calendar = await CreateAsync();
        _t.Detach();

        await _calendars.DeleteAsync(calendar.Id);
        _t.Detach();

        _t.Clock.Advance(Duration.FromDays(31));

        Assert.Equal(1, await _calendars.PurgeTrashAsync());
        _t.Detach();

        Assert.False(await _t.Db.Calendars.AnyAsync(c => c.Id == calendar.Id));
    }

    [Fact]
    public async Task Suresi_dolmayan_takvim_kalir()
    {
        var calendar = await CreateAsync();
        _t.Detach();

        await _calendars.DeleteAsync(calendar.Id);
        _t.Detach();

        _t.Clock.Advance(Duration.FromDays(5));

        Assert.Equal(0, await _calendars.PurgeTrashAsync());
    }

    // ==================================================================
    // Kullanım özeti
    // ==================================================================

    [Fact]
    public async Task Kullanim_ozeti_etkinlik_ve_paylasim_sayar()
    {
        var calendar = await CreateAsync();
        _t.Detach();

        var input = _t.Input("2026-03-02 10:00", "2026-03-02 11:00") with { CalendarId = calendar.Id };
        await _t.Events.CreateAsync(input);
        _t.Detach();

        var (otherUserId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");
        _t.Share(calendar.Id, otherUserId, SharingLevel.FullDetails);

        var usage = await _calendars.GetUsageAsync(calendar.Id);

        Assert.Equal(1, usage.EventCount);
        Assert.Equal(1, usage.ShareCount);
    }

    [Fact]
    public async Task Silinen_etkinlik_ozette_sayilmaz()
    {
        var calendar = await CreateAsync();
        _t.Detach();

        var input = _t.Input("2026-03-02 10:00", "2026-03-02 11:00") with { CalendarId = calendar.Id };
        var created = await _t.Events.CreateAsync(input);
        _t.Detach();

        await _t.Events.DeleteAsync(created.PrimaryEventId, null, SeriesEditScope.AllInSeries, _t.UserId);
        _t.Detach();

        Assert.Equal(0, (await _calendars.GetUsageAsync(calendar.Id)).EventCount);
    }

    // ==================================================================
    // Görünürlük
    // ==================================================================

    [Fact]
    public async Task Baska_kullanicinin_takvimi_listede_gorunmez()
    {
        await CreateAsync();
        _t.Detach();

        var (otherUserId, otherCalendarId) = _t.AddUser("Ayşe", "ayse@ornek.local");

        var own = await _calendars.GetOwnAsync(otherUserId);

        Assert.Single(own);
        Assert.Equal(otherCalendarId, own[0].Id);
    }

    [Fact]
    public async Task Yeni_takvim_izin_katmaninda_gorunur()
    {
        var calendar = await CreateAsync();
        _t.Detach();

        var visible = await _t.Permissions.GetVisibleCalendarsAsync(_t.UserId);

        Assert.Contains(visible, c => c.Id == calendar.Id);
    }
}
