using Takvim.Core.Domain;
using Takvim.Core.Permissions;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// Kullanıcılar arası veri yalıtımı. Bu testler güvenlik sınırını korur:
/// sorgu katmanı, izin motorunu atlayan bir yol bırakırsa burada kırılır.
/// </summary>
public class SharingTests : IDisposable
{
    private readonly TestDatabase _t = new();
    private readonly Guid _ayse;

    public SharingTests()
    {
        // Ayşe'nin kendi takvimi var; testler onun gözünden bakar.
        (_ayse, _) = _t.AddUser("Ayşe Yılmaz", "ayse@ornek.local");
    }

    public void Dispose()
    {
        _t.Dispose();
        GC.SuppressFinalize(this);
    }

    private const string Window = "2026-03-01 00:00";
    private const string WindowEnd = "2026-03-08 00:00";

    private Task<List<Takvim.Core.Recurrence.EventOccurrence>> SeenByAyse()
        => _t.SeenByAsync(_ayse, Window, WindowEnd);

    private async Task<Guid> CreateEventAsync(
        string title = "Bütçe toplantısı",
        EventVisibility visibility = EventVisibility.Default,
        Availability availability = Availability.Busy)
    {
        var input = _t.Input("2026-03-02 10:00", "2026-03-02 11:00", title) with
        {
            Visibility = visibility,
            Availability = availability,
            LocationText = "3. kat",
            DescriptionHtml = "Gizli gündem",
        };

        var created = await _t.Events.CreateAsync(input);
        _t.Detach();
        return created.PrimaryEventId;
    }

    // ==================================================================
    // Paylaşılmamış takvim
    // ==================================================================

    [Fact]
    public async Task Paylasilmamis_takvim_baskasina_hic_gorunmez()
    {
        await CreateEventAsync();

        Assert.Empty(await SeenByAyse());
    }

    [Fact]
    public async Task Sahibi_kendi_etkinligini_tam_gorur()
    {
        await CreateEventAsync();

        var mine = await _t.SeenByAsync(_t.UserId, Window, WindowEnd);

        Assert.Single(mine);
        Assert.Equal("Bütçe toplantısı", mine[0].DisplayTitle);
        Assert.True(mine[0].CanSeeDetails);
        Assert.True(mine[0].CanEdit);
    }

    // ==================================================================
    // Paylaşım seviyeleri
    // ==================================================================

    [Fact]
    public async Task Mesgul_musait_seviyesinde_baslik_karartilir()
    {
        await CreateEventAsync();
        _t.Share(_t.CalendarId, _ayse, SharingLevel.FreeBusy);

        var seen = await SeenByAyse();

        Assert.Single(seen);
        Assert.Equal("Meşgul", seen[0].DisplayTitle);
        Assert.False(seen[0].CanSeeDetails);
        Assert.False(seen[0].CanEdit);
    }

    [Fact]
    public async Task Karartilan_ornekte_ham_veri_de_temizlenir()
    {
        // Arayüz yanlışlıkla Source.Title okusa bile gizli veri görünmemeli.
        await CreateEventAsync();
        _t.Share(_t.CalendarId, _ayse, SharingLevel.FreeBusy);

        var seen = (await SeenByAyse())[0];

        Assert.Equal("Meşgul", seen.Source.Title);
        Assert.Null(seen.Source.LocationText);
        Assert.Null(seen.Source.DescriptionHtml);
        Assert.Equal(string.Empty, seen.Source.SearchText);
    }

    [Fact]
    public async Task Baslik_ve_konum_seviyesinde_aciklama_gizli_kalir()
    {
        await CreateEventAsync();
        _t.Share(_t.CalendarId, _ayse, SharingLevel.TitleLocation);

        var seen = (await SeenByAyse())[0];

        Assert.Equal("Bütçe toplantısı", seen.DisplayTitle);
        Assert.Equal("3. kat", seen.DisplayLocation);
        Assert.Null(seen.Source.DescriptionHtml);
        Assert.False(seen.CanSeeDetails);
    }

    [Fact]
    public async Task Tum_detaylar_seviyesinde_her_sey_gorunur()
    {
        await CreateEventAsync();
        _t.Share(_t.CalendarId, _ayse, SharingLevel.FullDetails);

        var seen = (await SeenByAyse())[0];

        Assert.Equal("Bütçe toplantısı", seen.DisplayTitle);
        Assert.Equal("Gizli gündem", seen.Source.DescriptionHtml);
        Assert.True(seen.CanSeeDetails);
        Assert.False(seen.CanEdit);
    }

    [Fact]
    public async Task Duzenleme_seviyesinde_yetki_verilir()
    {
        await CreateEventAsync();
        _t.Share(_t.CalendarId, _ayse, SharingLevel.CanEdit);

        Assert.True((await SeenByAyse())[0].CanEdit);
    }

    // ==================================================================
    // Özel etkinlikler
    // ==================================================================

    [Fact]
    public async Task Ozel_etkinlik_tum_detay_paylasiminda_bile_karartilir()
    {
        await CreateEventAsync(visibility: EventVisibility.Private);
        _t.Share(_t.CalendarId, _ayse, SharingLevel.FullDetails);

        var seen = (await SeenByAyse())[0];

        Assert.Equal("Meşgul", seen.DisplayTitle);
        Assert.False(seen.CanSeeDetails);
    }

    [Fact]
    public async Task Musait_ozel_etkinlik_hic_gorunmez()
    {
        await CreateEventAsync(visibility: EventVisibility.Private, availability: Availability.Free);
        _t.Share(_t.CalendarId, _ayse, SharingLevel.FullDetails);

        Assert.Empty(await SeenByAyse());
    }

    [Fact]
    public async Task Herkese_acik_etkinlik_dusuk_seviyede_bile_gorunur()
    {
        await CreateEventAsync(visibility: EventVisibility.Public);
        _t.Share(_t.CalendarId, _ayse, SharingLevel.FreeBusy);

        var seen = (await SeenByAyse())[0];

        Assert.Equal("Bütçe toplantısı", seen.DisplayTitle);
        Assert.True(seen.CanSeeDetails);
    }

    [Fact]
    public async Task Gizli_kategorili_etkinlik_karartilir()
    {
        var category = new Category { OwnerUserId = _t.UserId, Name = "Sağlık", IsPrivate = true };
        _t.Db.Categories.Add(category);
        _t.Db.SaveChanges();
        _t.Detach();

        var input = _t.Input("2026-03-02 10:00", "2026-03-02 11:00", "Doktor") with
        {
            CategoryIds = [category.Id],
        };

        await _t.Events.CreateAsync(input);
        _t.Detach();

        _t.Share(_t.CalendarId, _ayse, SharingLevel.FullDetails);

        Assert.Equal("Meşgul", (await SeenByAyse())[0].DisplayTitle);
    }

    // ==================================================================
    // Vekil erişimi
    // ==================================================================

    [Fact]
    public async Task Vekil_ozel_etkinligi_izinsiz_goremez()
    {
        await CreateEventAsync(visibility: EventVisibility.Private);
        _t.Share(_t.CalendarId, _ayse, SharingLevel.CanEdit, isDelegate: true);

        Assert.Equal("Meşgul", (await SeenByAyse())[0].DisplayTitle);
    }

    [Fact]
    public async Task Ozel_ogeleri_gorebilen_vekil_gorur()
    {
        await CreateEventAsync(visibility: EventVisibility.Private);
        _t.Share(_t.CalendarId, _ayse, SharingLevel.CanEdit, isDelegate: true, canSeePrivate: true);

        var seen = (await SeenByAyse())[0];

        Assert.Equal("Bütçe toplantısı", seen.DisplayTitle);
        Assert.True(seen.CanEdit);
    }

    // ==================================================================
    // Katılımcılık: paylaşımdan bağımsız erişim yolu
    // ==================================================================

    [Fact]
    public async Task Davetli_paylasilmamis_takvimdeki_etkinligi_gorur()
    {
        var eventId = await CreateEventAsync();

        await _t.Attendees.SyncAsync(eventId,
            [new AttendeeInput("ayse@ornek.local", "Ayşe Yılmaz", _ayse)], _t.UserId);
        _t.Detach();

        var seen = await SeenByAyse();

        Assert.Single(seen);
        Assert.Equal("Bütçe toplantısı", seen[0].DisplayTitle);
        Assert.True(seen[0].CanSeeDetails);
        // Davetli olmak düzenleme yetkisi vermez.
        Assert.False(seen[0].CanEdit);
    }

    [Fact]
    public async Task Davetli_ozel_etkinligi_de_gorur()
    {
        // Davet edildiyse zaten toplantıyı bilmesi gerekir.
        var eventId = await CreateEventAsync(visibility: EventVisibility.Private);

        await _t.Attendees.SyncAsync(eventId,
            [new AttendeeInput("ayse@ornek.local", "Ayşe Yılmaz", _ayse)], _t.UserId);
        _t.Detach();

        Assert.Equal("Bütçe toplantısı", (await SeenByAyse())[0].DisplayTitle);
    }

    [Fact]
    public async Task Davetli_olmayan_ucuncu_kisi_goremez()
    {
        var (mehmet, _) = _t.AddUser("Mehmet Kaya", "mehmet@ornek.local");
        var eventId = await CreateEventAsync();

        await _t.Attendees.SyncAsync(eventId,
            [new AttendeeInput("ayse@ornek.local", "Ayşe Yılmaz", _ayse)], _t.UserId);
        _t.Detach();

        Assert.Empty(await _t.SeenByAsync(mehmet, Window, WindowEnd));
    }

    // ==================================================================
    // Çöp kutusu ve arama yalıtımı
    // ==================================================================

    [Fact]
    public async Task Cop_kutusu_yalnizca_kendi_kayitlarini_gosterir()
    {
        var eventId = await CreateEventAsync();
        await _t.Events.DeleteAsync(eventId, null, SeriesEditScope.AllInSeries, _t.UserId);
        _t.Detach();

        _t.Share(_t.CalendarId, _ayse, SharingLevel.FullControl);

        Assert.Single(await _t.Query.GetTrashAsync(_t.UserId));
        Assert.Empty(await _t.Query.GetTrashAsync(_ayse));
    }

    [Fact]
    public async Task Karartilan_etkinlik_aramada_cikmaz()
    {
        await CreateEventAsync(visibility: EventVisibility.Private);
        _t.Share(_t.CalendarId, _ayse, SharingLevel.FullDetails);

        var found = await _t.Query.GetOccurrencesAsync(
            _ayse, _t.Utc(Window), _t.Utc(WindowEnd),
            new OccurrenceFilter { SearchTerm = "bütçe" });

        Assert.Empty(found);
    }

    [Fact]
    public async Task Gorulebilen_etkinlik_aramada_cikar()
    {
        await CreateEventAsync();
        _t.Share(_t.CalendarId, _ayse, SharingLevel.FullDetails);

        var found = await _t.Query.GetOccurrencesAsync(
            _ayse, _t.Utc(Window), _t.Utc(WindowEnd),
            new OccurrenceFilter { SearchTerm = "bütçe" });

        Assert.Single(found);
    }

    // ==================================================================
    // Erişim haritası
    // ==================================================================

    [Fact]
    public async Task Erisim_haritasi_kendi_ve_paylasilan_takvimleri_toplar()
    {
        _t.Share(_t.CalendarId, _ayse, SharingLevel.FreeBusy);

        var scope = await _t.Permissions.LoadAsync(_ayse);

        // Ayşe'nin kendi takvimi + paylaşılan takvim.
        Assert.Equal(2, scope.ReadableCalendarIds.Count());
        Assert.Single(scope.OwnedCalendarIds);
        Assert.Equal(SharingLevel.FreeBusy, scope.LevelFor(_t.CalendarId));
        Assert.False(scope.CanWriteTo(_t.CalendarId));
    }

    [Fact]
    public async Task Duzenleme_seviyesi_yazma_yetkisi_verir()
    {
        _t.Share(_t.CalendarId, _ayse, SharingLevel.CanEdit);

        var scope = await _t.Permissions.LoadAsync(_ayse);

        Assert.True(scope.CanWriteTo(_t.CalendarId));
        Assert.Contains(_t.CalendarId, scope.WritableCalendarIds);
    }
}
