using Microsoft.EntityFrameworkCore;
using Takvim.Core.Domain;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// Oda ve ekipman rezervasyonu. Her kaynağın kendi takvimi olduğu için asıl
/// sınanan şey iki kaydın (toplantı ve tutma) birlikte hareket etmesi: toplantı
/// taşınınca tutma taşınmalı, silinince serbest kalmalı, reddedilen bir talep
/// odayı hiç işgal etmemeli.
/// </summary>
public class ResourceTests : IDisposable
{
    private readonly TestDatabase _t = new();
    private readonly ResourceService _resources;

    public ResourceTests()
        => _resources = new ResourceService(_t.Db, _t.Events, _t.Zones, _t.Clock);

    public void Dispose()
    {
        _t.Dispose();
        GC.SuppressFinalize(this);
    }

    private const string Start = "2026-03-02 10:00";
    private const string End = "2026-03-02 11:00";

    private async Task<Resource> CreateRoomAsync(
        string name = "Toplantı Odası A",
        int? capacity = 8,
        ResourceFeatures features = ResourceFeatures.Projector,
        BookingPolicy policy = BookingPolicy.AutoAccept,
        string? location = "3. kat")
    {
        var resource = await _resources.CreateAsync(new ResourceInput
        {
            Name = name,
            Capacity = capacity,
            Features = features,
            Policy = policy,
            Location = location,
        }, _t.UserId);

        _t.Detach();
        return resource;
    }

    private async Task<Guid> CreateMeetingAsync(string start = Start, string end = End, string title = "Toplantı")
    {
        var created = await _t.Events.CreateAsync(_t.Input(start, end, title));
        _t.Detach();

        return created.PrimaryEventId;
    }

    // ==================================================================
    // Tanımlar
    // ==================================================================

    [Fact]
    public async Task Kaynak_kendi_takvimiyle_acilir()
    {
        var room = await CreateRoomAsync();

        var calendar = await _t.Db.Calendars.AsNoTracking().FirstAsync(c => c.Id == room.CalendarId);

        Assert.Equal(CalendarKind.Resource, calendar.Kind);
        Assert.Equal("Toplantı Odası A", calendar.Name);

        // Kimsenin kişisel takvimi değil; kenar çubuğunda gizli başlar.
        Assert.False(calendar.IsVisible);
    }

    [Fact]
    public async Task Ekipmanin_kapasitesi_olmaz()
    {
        var projector = await _resources.CreateAsync(new ResourceInput
        {
            Name = "Taşınabilir projeksiyon",
            Kind = ResourceKind.Equipment,
            Capacity = 5,
        }, _t.UserId);

        Assert.Null(projector.Capacity);
    }

    [Fact]
    public async Task Bos_ad_reddedilir()
        => await Assert.ThrowsAsync<ArgumentException>(
            () => _resources.CreateAsync(new ResourceInput { Name = "  " }, _t.UserId));

    [Fact]
    public async Task Kaynak_guncellenince_takvim_adi_da_degisir()
    {
        var room = await CreateRoomAsync();

        await _resources.UpdateAsync(room.Id, new ResourceInput { Name = "Yeni Ad", Capacity = 4 });
        _t.Detach();

        var calendar = await _t.Db.Calendars.AsNoTracking().FirstAsync(c => c.Id == room.CalendarId);

        Assert.Equal("Yeni Ad", calendar.Name);
    }

    [Fact]
    public async Task Kapatilan_kaynak_listede_cikmaz()
    {
        var room = await CreateRoomAsync();

        await _resources.SetActiveAsync(room.Id, false);
        _t.Detach();

        Assert.Empty(await _resources.GetAllAsync());
        Assert.Single(await _resources.GetAllAsync(includeInactive: true));
    }

    [Fact]
    public async Task Kaynak_silinince_takvimi_de_gider()
    {
        var room = await CreateRoomAsync();

        await _resources.DeleteAsync(room.Id);
        _t.Detach();

        Assert.Empty(await _resources.GetAllAsync(includeInactive: true));
        Assert.False(await _t.Db.Calendars.AnyAsync(c => c.Id == room.CalendarId));
    }

    [Fact]
    public async Task Silinen_kaynagin_rezervasyonlari_da_gider()
    {
        var room = await CreateRoomAsync();
        var meetingId = await CreateMeetingAsync();

        await _resources.BookAsync(room.Id, meetingId, _t.UserId);
        _t.Detach();

        await _resources.DeleteAsync(room.Id);
        _t.Detach();

        Assert.Empty(await _t.Db.ResourceBookings.ToListAsync());
    }

    // ==================================================================
    // Arama
    // ==================================================================

    [Fact]
    public async Task Kapasiteye_gore_suzulur()
    {
        await CreateRoomAsync("Küçük", capacity: 4);
        await CreateRoomAsync("Büyük", capacity: 20);

        var found = await _resources.FindAvailableAsync(
            _t.Utc(Start), _t.Utc(End), new ResourceQuery { MinimumCapacity = 10 });

        Assert.Equal("Büyük", Assert.Single(found).Resource.Name);
    }

    [Fact]
    public async Task Ozelliklerin_hepsi_aranir()
    {
        await CreateRoomAsync("Yalnız projeksiyon", features: ResourceFeatures.Projector);
        await CreateRoomAsync("İkisi de",
            features: ResourceFeatures.Projector | ResourceFeatures.VideoConference);

        var found = await _resources.FindAvailableAsync(
            _t.Utc(Start), _t.Utc(End),
            new ResourceQuery
            {
                RequiredFeatures = ResourceFeatures.Projector | ResourceFeatures.VideoConference,
            });

        Assert.Equal("İkisi de", Assert.Single(found).Resource.Name);
    }

    [Fact]
    public async Task Metin_ad_ve_konumda_aranir()
    {
        await CreateRoomAsync("Ada", location: "1. kat");
        await CreateRoomAsync("Karadeniz", location: "Ada binası");
        await CreateRoomAsync("Ege", location: "2. kat");

        var found = await _resources.FindAvailableAsync(
            _t.Utc(Start), _t.Utc(End), new ResourceQuery { Term = "ada" });

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public async Task Turkce_arama_aksansiz_da_bulur()
    {
        await CreateRoomAsync("Çınar Salonu");

        var found = await _resources.FindAvailableAsync(
            _t.Utc(Start), _t.Utc(End), new ResourceQuery { Term = "cinar" });

        Assert.Single(found);
    }

    [Fact]
    public async Task Ture_gore_suzulur()
    {
        await CreateRoomAsync("Oda");
        await _resources.CreateAsync(
            new ResourceInput { Name = "Kamera", Kind = ResourceKind.Equipment }, _t.UserId);
        _t.Detach();

        var found = await _resources.FindAvailableAsync(
            _t.Utc(Start), _t.Utc(End), new ResourceQuery { Kind = ResourceKind.Equipment });

        Assert.Equal("Kamera", Assert.Single(found).Resource.Name);
    }

    [Fact]
    public async Task Bos_kaynaklar_once_gelir()
    {
        var busy = await CreateRoomAsync("Aaa Dolu");
        await CreateRoomAsync("Zzz Boş");

        await _resources.BookAsync(busy.Id, await CreateMeetingAsync(), _t.UserId);
        _t.Detach();

        var found = await _resources.FindAvailableAsync(_t.Utc(Start), _t.Utc(End));

        // Ad sırası "Aaa" önce derdi; doluluk ondan baskın.
        Assert.Equal("Zzz Boş", found[0].Resource.Name);
        Assert.True(found[0].IsFree);
        Assert.False(found[1].IsFree);
    }

    [Fact]
    public async Task Dolu_kaynagin_cakisan_toplantisi_bildirilir()
    {
        var room = await CreateRoomAsync();

        await _resources.BookAsync(room.Id, await CreateMeetingAsync(title: "Bütçe"), _t.UserId);
        _t.Detach();

        var found = Assert.Single(await _resources.FindAvailableAsync(_t.Utc(Start), _t.Utc(End)));

        Assert.Equal("Bütçe", found.ConflictTitle);
    }

    // ==================================================================
    // Rezervasyon
    // ==================================================================

    [Fact]
    public async Task Bos_oda_kendiliginden_kabul_edilir()
    {
        var room = await CreateRoomAsync();
        var meetingId = await CreateMeetingAsync();

        var result = await _resources.BookAsync(room.Id, meetingId, _t.UserId);
        _t.Detach();

        Assert.True(result.Success);
        Assert.Equal(ResourceBookingStatus.Accepted, result.Booking!.Status);
        Assert.NotNull(result.Booking.HoldEventId);
    }

    [Fact]
    public async Task Tutma_kaydi_kaynak_takvimine_yazilir()
    {
        var room = await CreateRoomAsync();
        var result = await _resources.BookAsync(room.Id, await CreateMeetingAsync(), _t.UserId);
        _t.Detach();

        var hold = await _t.Db.Events.AsNoTracking().FirstAsync(e => e.Id == result.Booking!.HoldEventId);

        Assert.Equal(room.CalendarId, hold.CalendarId);
        Assert.Equal("Toplantı Odası A", hold.LocationText);

        // Oda takvimini görebilen herkes toplantının içeriğini görmemeli.
        Assert.Equal(EventVisibility.Private, hold.Visibility);
        Assert.Null(hold.DescriptionHtml);
    }

    [Fact]
    public async Task Dolu_oda_ikinci_kez_verilmez()
    {
        var room = await CreateRoomAsync();

        await _resources.BookAsync(room.Id, await CreateMeetingAsync(), _t.UserId);
        _t.Detach();

        var second = await _resources.BookAsync(room.Id, await CreateMeetingAsync(), _t.UserId);

        Assert.False(second.Success);
        Assert.Contains("dolu", second.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bitisik_aralik_cakisma_sayilmaz()
    {
        var room = await CreateRoomAsync();

        await _resources.BookAsync(room.Id, await CreateMeetingAsync(), _t.UserId);
        _t.Detach();

        // 10:00-11:00 ile 11:00-12:00 çakışmaz.
        var next = await CreateMeetingAsync("2026-03-02 11:00", "2026-03-02 12:00");
        var result = await _resources.BookAsync(room.Id, next, _t.UserId);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Ayni_toplantiya_iki_kez_tutmak_yeni_kayit_acmaz()
    {
        var room = await CreateRoomAsync();
        var meetingId = await CreateMeetingAsync();

        var first = await _resources.BookAsync(room.Id, meetingId, _t.UserId);
        _t.Detach();
        var second = await _resources.BookAsync(room.Id, meetingId, _t.UserId);
        _t.Detach();

        Assert.Equal(first.Booking!.Id, second.Booking!.Id);
        Assert.Single(await _t.Db.ResourceBookings.ToListAsync());
    }

    [Fact]
    public async Task Kapatilan_kaynak_tutulamaz()
    {
        var room = await CreateRoomAsync();
        await _resources.SetActiveAsync(room.Id, false);
        _t.Detach();

        var result = await _resources.BookAsync(room.Id, await CreateMeetingAsync(), _t.UserId);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Olmayan_etkinlige_kaynak_tutulamaz()
    {
        var room = await CreateRoomAsync();

        Assert.False((await _resources.BookAsync(room.Id, Guid.NewGuid(), _t.UserId)).Success);
    }

    // ==================================================================
    // Onay
    // ==================================================================

    [Fact]
    public async Task Onay_isteyen_kaynak_beklemede_kalir()
    {
        var room = await CreateRoomAsync(policy: BookingPolicy.RequiresApproval);

        var result = await _resources.BookAsync(room.Id, await CreateMeetingAsync(), _t.UserId);
        _t.Detach();

        Assert.True(result.IsPending);

        // Onaylanmamış talep odayı işgal etmemeli.
        Assert.Null(result.Booking!.HoldEventId);
    }

    [Fact]
    public async Task Bekleyen_talep_odayi_isgal_etmez()
    {
        var room = await CreateRoomAsync(policy: BookingPolicy.RequiresApproval);

        await _resources.BookAsync(room.Id, await CreateMeetingAsync(), _t.UserId);
        _t.Detach();

        // Aynı saate ikinci talep de alınabilmeli; hangisinin verileceğine
        // sorumlu karar verir.
        var second = await _resources.BookAsync(room.Id, await CreateMeetingAsync(), _t.UserId);

        Assert.True(second.IsPending);
    }

    [Fact]
    public async Task Onaylanan_talep_tutmaya_donusur()
    {
        var room = await CreateRoomAsync(policy: BookingPolicy.RequiresApproval);
        var pending = await _resources.BookAsync(room.Id, await CreateMeetingAsync(), _t.UserId);
        _t.Detach();

        var result = await _resources.RespondAsync(pending.Booking!.Id, accept: true, _t.UserId);
        _t.Detach();

        Assert.Equal(ResourceBookingStatus.Accepted, result.Booking!.Status);
        Assert.NotNull(result.Booking.HoldEventId);
    }

    [Fact]
    public async Task Reddedilen_talep_oda_birakir()
    {
        var room = await CreateRoomAsync(policy: BookingPolicy.RequiresApproval);
        var pending = await _resources.BookAsync(room.Id, await CreateMeetingAsync(), _t.UserId);
        _t.Detach();

        await _resources.RespondAsync(pending.Booking!.Id, accept: false, _t.UserId, "Bakımda");
        _t.Detach();

        var found = Assert.Single(await _resources.FindAvailableAsync(_t.Utc(Start), _t.Utc(End)));

        Assert.True(found.IsFree);
    }

    [Fact]
    public async Task Onay_beklerken_kapilan_oda_verilmez()
    {
        var room = await CreateRoomAsync(policy: BookingPolicy.RequiresApproval);

        var first = await _resources.BookAsync(room.Id, await CreateMeetingAsync(), _t.UserId);
        _t.Detach();
        var second = await _resources.BookAsync(room.Id, await CreateMeetingAsync(), _t.UserId);
        _t.Detach();

        await _resources.RespondAsync(first.Booking!.Id, accept: true, _t.UserId);
        _t.Detach();

        // İlk talep odayı aldı; ikincisi onaylanamaz.
        var result = await _resources.RespondAsync(second.Booking!.Id, accept: true, _t.UserId);

        Assert.False(result.Success);
        Assert.Equal(ResourceBookingStatus.Declined, result.Booking!.Status);
    }

    [Fact]
    public async Task Bekleyen_talepler_sorumluya_listelenir()
    {
        var room = await CreateRoomAsync(policy: BookingPolicy.RequiresApproval);
        await _resources.BookAsync(room.Id, await CreateMeetingAsync(), _t.UserId);
        _t.Detach();

        // Sorumlu verilmemişse takvimin sahibi karşılar.
        Assert.Single(await _resources.GetPendingAsync(_t.UserId));
    }

    [Fact]
    public async Task Baskasinin_kuyrugunda_gorunmez()
    {
        var room = await CreateRoomAsync(policy: BookingPolicy.RequiresApproval);
        await _resources.BookAsync(room.Id, await CreateMeetingAsync(), _t.UserId);
        _t.Detach();

        var (otherUserId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");

        Assert.Empty(await _resources.GetPendingAsync(otherUserId));
    }

    // ==================================================================
    // Taşıma ve bırakma
    // ==================================================================

    [Fact]
    public async Task Toplanti_tasininca_tutma_da_tasinir()
    {
        var room = await CreateRoomAsync();
        var meetingId = await CreateMeetingAsync();

        var booking = await _resources.BookAsync(room.Id, meetingId, _t.UserId);
        _t.Detach();

        await _t.Events.UpdateAsync(meetingId, null, SeriesEditScope.AllInSeries,
            _t.Input("2026-03-02 14:00", "2026-03-02 15:00"));
        _t.Detach();

        var released = await _resources.RescheduleAsync(meetingId, _t.UserId);
        _t.Detach();

        Assert.Empty(released);

        var hold = await _t.Db.Events.AsNoTracking()
            .FirstAsync(e => e.Id == booking.Booking!.HoldEventId);

        Assert.Equal(TestDatabase.Parse("2026-03-02 14:00"), hold.StartLocal);
    }

    [Fact]
    public async Task Yeni_saatte_dolu_kaynak_birakilir()
    {
        var room = await CreateRoomAsync();

        // Öğleden sonrası başka bir toplantıya verilmiş.
        var other = await CreateMeetingAsync("2026-03-02 14:00", "2026-03-02 15:00", "Başkası");
        await _resources.BookAsync(room.Id, other, _t.UserId);
        _t.Detach();

        var mine = await CreateMeetingAsync();
        await _resources.BookAsync(room.Id, mine, _t.UserId);
        _t.Detach();

        await _t.Events.UpdateAsync(mine, null, SeriesEditScope.AllInSeries,
            _t.Input("2026-03-02 14:00", "2026-03-02 15:00"));
        _t.Detach();

        var released = await _resources.RescheduleAsync(mine, _t.UserId);
        _t.Detach();

        // Sessizce eski saatte tutmaya devam etmek, kullanıcının odası olduğunu
        // sanmasına yol açardı.
        Assert.Equal("Toplantı Odası A", Assert.Single(released));
        Assert.Empty(await _resources.GetForEventAsync(mine));
    }

    [Fact]
    public async Task Birakilan_kaynagin_tutmasi_da_silinir()
    {
        var room = await CreateRoomAsync();
        var meetingId = await CreateMeetingAsync();

        var booking = await _resources.BookAsync(room.Id, meetingId, _t.UserId);
        _t.Detach();

        await _resources.ReleaseAsync(booking.Booking!.Id, _t.UserId);
        _t.Detach();

        Assert.Empty(await _resources.GetForEventAsync(meetingId));

        var hold = await _t.Db.Events.AsNoTracking()
            .FirstAsync(e => e.Id == booking.Booking.HoldEventId);

        Assert.NotNull(hold.DeletedAt);
    }

    [Fact]
    public async Task Toplantinin_tum_kaynaklari_birakilir()
    {
        var first = await CreateRoomAsync("Oda 1");
        var second = await CreateRoomAsync("Oda 2");
        var meetingId = await CreateMeetingAsync();

        await _resources.BookAsync(first.Id, meetingId, _t.UserId);
        _t.Detach();
        await _resources.BookAsync(second.Id, meetingId, _t.UserId);
        _t.Detach();

        Assert.Equal(2, (await _resources.GetForEventAsync(meetingId)).Count);

        await _resources.ReleaseAllAsync(meetingId, _t.UserId);
        _t.Detach();

        Assert.Empty(await _resources.GetForEventAsync(meetingId));
    }

    [Fact]
    public async Task Birakilan_oda_yeniden_verilebilir()
    {
        var room = await CreateRoomAsync();

        var first = await _resources.BookAsync(room.Id, await CreateMeetingAsync(), _t.UserId);
        _t.Detach();

        await _resources.ReleaseAsync(first.Booking!.Id, _t.UserId);
        _t.Detach();

        var second = await _resources.BookAsync(room.Id, await CreateMeetingAsync(), _t.UserId);

        Assert.True(second.Success);
    }

    // ==================================================================
    // Biçimlendirme
    // ==================================================================

    [Fact]
    public void Ozet_okunur_bicimde_kurulur()
    {
        var room = new Resource
        {
            Name = "A",
            Location = "3. kat",
            Capacity = 8,
            Features = ResourceFeatures.Projector | ResourceFeatures.VideoConference,
        };

        Assert.Equal("3. kat · 8 kişi · 📽 📹", room.Summary);
    }

    [Fact]
    public void Eksik_alanlar_ozette_bosluk_birakmaz()
        => Assert.Equal("4 kişi", new Resource { Name = "A", Capacity = 4 }.Summary);
}
