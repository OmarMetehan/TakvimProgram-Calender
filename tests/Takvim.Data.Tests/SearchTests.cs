using Takvim.Core.Domain;
using Takvim.Core.Permissions;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// Gelişmiş arama. Ölçütlerin doğru süzmesi kadar önemli olan şey, aramanın
/// izin katmanını atlayamamasıdır: içeriğe bakan bir ölçüt, içeriği
/// görülemeyen bir etkinliği sonuçta göstermemelidir.
/// </summary>
public class SearchTests : IDisposable
{
    private readonly TestDatabase _t = new();
    private readonly SearchService _search;

    public SearchTests()
        => _search = new SearchService(_t.Db, _t.Query, _t.Zones, _t.Clock);

    public void Dispose()
    {
        _t.Dispose();
        GC.SuppressFinalize(this);
    }

    // Sınama saati 2026-01-01 09:00 UTC'de duruyor.
    private const string Future = "2026-06-10 10:00";
    private const string Past = "2025-06-10 10:00";

    private Task<List<Takvim.Core.Recurrence.EventOccurrence>> RunAsync(SearchCriteria criteria)
        => _search.RunAsync(_t.UserId, criteria, "Europe/Istanbul");

    private async Task<Guid> CreateAsync(
        string start = Future, string title = "Bütçe toplantısı", string? rrule = null, bool allDay = false)
    {
        var result = await _t.Events.CreateAsync(
            _t.Input(start, AddHour(start), title, rrule, allDay));

        _t.Detach();
        return result.PrimaryEventId;
    }

    private static string AddHour(string value)
        => TestDatabase.Parse(value).PlusHours(1).ToString("uuuu-MM-dd HH:mm", null);

    // ==================================================================
    // Aralık
    // ==================================================================

    [Fact]
    public async Task Bos_olcut_arama_calistirmaz()
        => Assert.Empty(await RunAsync(new SearchCriteria()));

    [Fact]
    public async Task Gelecek_aramasi_gecmisi_getirmez()
    {
        await CreateAsync(Future, "İleri");
        await CreateAsync(Past, "Geri");

        var found = await RunAsync(new SearchCriteria { Term = "i", Range = SearchRange.Upcoming });

        Assert.Equal("İleri", Assert.Single(found).Source.Title);
    }

    [Fact]
    public async Task Gecmis_aramasi_gelecegi_getirmez()
    {
        await CreateAsync(Future, "İleri");
        await CreateAsync(Past, "Geri");

        var found = await RunAsync(new SearchCriteria { Term = "i", Range = SearchRange.Past });

        Assert.Equal("Geri", Assert.Single(found).Source.Title);
    }

    [Fact]
    public async Task Tumu_ikisini_de_getirir()
    {
        await CreateAsync(Future, "İleri");
        await CreateAsync(Past, "Geri");

        Assert.Equal(2, (await RunAsync(new SearchCriteria { Term = "i", Range = SearchRange.All })).Count);
    }

    [Fact]
    public async Task Bu_yil_aramasi_yalnizca_o_yili_kapsar()
    {
        await CreateAsync("2026-03-01 10:00", "Bu yıl");
        await CreateAsync("2025-03-01 10:00", "Geçen yıl");
        await CreateAsync("2027-03-01 10:00", "Gelecek yıl");

        var found = await RunAsync(new SearchCriteria { Term = "yıl", Range = SearchRange.ThisYear });

        Assert.Equal("Bu yıl", Assert.Single(found).Source.Title);
    }

    [Fact]
    public async Task Gelecek_aramasi_yakindan_uzaga_siralanir()
    {
        await CreateAsync("2026-09-01 10:00", "Sonra");
        await CreateAsync("2026-03-01 10:00", "Önce");

        var found = await RunAsync(new SearchCriteria { Term = "n", Range = SearchRange.Upcoming });

        Assert.Equal(["Önce", "Sonra"], found.Select(o => o.Source.Title));
    }

    [Fact]
    public async Task Gecmis_aramasi_yeniden_eskiye_siralanir()
    {
        await CreateAsync("2025-09-01 10:00", "Yakın");
        await CreateAsync("2025-03-01 10:00", "Uzak");

        var found = await RunAsync(new SearchCriteria { Term = "a", Range = SearchRange.Past });

        Assert.Equal(["Yakın", "Uzak"], found.Select(o => o.Source.Title));
    }

    // ==================================================================
    // Ölçütler
    // ==================================================================

    [Fact]
    public async Task Tekrarlayan_olcutu_ayirir()
    {
        await CreateAsync(Future, "Tekil");
        await CreateAsync(Future, "Seri", "FREQ=WEEKLY;COUNT=3");

        var recurring = await RunAsync(new SearchCriteria { IsRecurring = true, Range = SearchRange.All });
        var single = await RunAsync(new SearchCriteria { IsRecurring = false, Range = SearchRange.All });

        Assert.All(recurring, o => Assert.Equal("Seri", o.Source.Title));
        Assert.Equal("Tekil", Assert.Single(single).Source.Title);
    }

    [Fact]
    public async Task Tum_gun_olcutu_ayirir()
    {
        await CreateAsync(Future, "Saatli");
        await _t.Events.CreateAsync(_t.Input("2026-06-11 00:00", "2026-06-12 00:00", "Tüm gün", allDay: true));
        _t.Detach();

        var found = await RunAsync(new SearchCriteria { IsAllDay = true, Range = SearchRange.All });

        Assert.Equal("Tüm gün", Assert.Single(found).Source.Title);
    }

    [Fact]
    public async Task Davetli_olcutu_ayirir()
    {
        var withGuests = await CreateAsync(Future, "Toplantı");
        await CreateAsync(Future, "Kişisel");

        var (guestUserId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");
        await _t.Attendees.SyncAsync(
            withGuests,
            [new AttendeeInput("ayse@ornek.local", "Ayşe", guestUserId)],
            _t.UserId);
        _t.Detach();

        var meetings = await RunAsync(new SearchCriteria { HasAttendees = true, Range = SearchRange.All });

        Assert.Equal("Toplantı", Assert.Single(meetings).Source.Title);
    }

    [Fact]
    public async Task Belirli_katilimci_aranir()
    {
        var target = await CreateAsync(Future, "Ayşe ile");
        await CreateAsync(Future, "Yalnız");

        var (guestUserId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");
        await _t.Attendees.SyncAsync(
            target, [new AttendeeInput("ayse@ornek.local", "Ayşe", guestUserId)], _t.UserId);
        _t.Detach();

        var found = await RunAsync(new SearchCriteria { AttendeeUserId = guestUserId, Range = SearchRange.All });

        Assert.Equal("Ayşe ile", Assert.Single(found).Source.Title);
    }

    [Fact]
    public async Task Cevrimici_olcutu_ayirir()
    {
        await CreateAsync(Future, "Yüz yüze");

        var input = _t.Input(Future, AddHour(Future), "Uzaktan") with
        {
            OnlineMeetingUrl = "https://meet.jit.si/abc",
        };

        await _t.Events.CreateAsync(input);
        _t.Detach();

        var found = await RunAsync(new SearchCriteria { HasOnlineMeeting = true, Range = SearchRange.All });

        Assert.Equal("Uzaktan", Assert.Single(found).Source.Title);
    }

    [Fact]
    public async Task Mesguliyet_olcutu_ayirir()
    {
        await CreateAsync(Future, "Meşgul");

        var input = _t.Input(Future, AddHour(Future), "Müsait") with { Availability = Availability.Free };
        await _t.Events.CreateAsync(input);
        _t.Detach();

        var found = await RunAsync(new SearchCriteria
        {
            Availabilities = [Availability.Free],
            Range = SearchRange.All,
        });

        Assert.Equal("Müsait", Assert.Single(found).Source.Title);
    }

    [Fact]
    public async Task Iptal_edilenler_varsayilan_olarak_gizli()
    {
        var cancelled = await CreateAsync(Future, "İptal olan");
        await _t.Events.CancelMeetingAsync(cancelled, "Gerek kalmadı", _t.UserId);
        _t.Detach();

        Assert.Empty(await RunAsync(new SearchCriteria { Term = "iptal", Range = SearchRange.All }));

        var found = await RunAsync(new SearchCriteria
        {
            Term = "iptal",
            Range = SearchRange.All,
            IncludeCancelled = true,
        });

        Assert.Single(found);
    }

    // ==================================================================
    // İzinler
    // ==================================================================

    [Fact]
    public async Task Icerige_bakan_olcut_detayi_gorulemeyeni_getirmez()
    {
        var eventId = await CreateAsync(Future, "Gizli toplantı");

        var (guestUserId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");
        await _t.Attendees.SyncAsync(
            eventId, [new AttendeeInput("ayse@ornek.local", "Ayşe", guestUserId)], _t.UserId);
        _t.Detach();

        // Ali yalnızca "meşgul" görüyor.
        var (viewerUserId, _) = _t.AddUser("Ali", "ali@ornek.local");
        _t.Share(_t.CalendarId, viewerUserId, SharingLevel.FreeBusy);

        var found = await _search.RunAsync(
            viewerUserId,
            new SearchCriteria { HasAttendees = true, Range = SearchRange.All },
            "Europe/Istanbul");

        // Aksi hâlde bloğun belirmesi "bu etkinliğin davetlisi var" bilgisini verirdi.
        Assert.Empty(found);
    }

    [Fact]
    public async Task Icerige_bakmayan_olcut_mesgul_bloklarini_getirir()
    {
        await CreateAsync(Future, "Gizli toplantı");

        var (viewerUserId, _) = _t.AddUser("Ali", "ali@ornek.local");
        _t.Share(_t.CalendarId, viewerUserId, SharingLevel.FreeBusy);

        var found = await _search.RunAsync(
            viewerUserId,
            new SearchCriteria { IsAllDay = false, Range = SearchRange.All },
            "Europe/Istanbul");

        // Meşguliyet zaten paylaşılmış bir bilgi; başlık karartılmış gelir.
        Assert.NotEqual("Gizli toplantı", Assert.Single(found).Source.Title);
    }

    [Fact]
    public async Task Metin_aramasi_karartilmis_ornegi_getirmez()
    {
        await CreateAsync(Future, "Bütçe");

        var (viewerUserId, _) = _t.AddUser("Ali", "ali@ornek.local");
        _t.Share(_t.CalendarId, viewerUserId, SharingLevel.FreeBusy);

        var found = await _search.RunAsync(
            viewerUserId,
            new SearchCriteria { Term = "bütçe", Range = SearchRange.All },
            "Europe/Istanbul");

        Assert.Empty(found);
    }

    // ==================================================================
    // Kaydedilmiş aramalar
    // ==================================================================

    [Fact]
    public async Task Arama_kaydedilir_ve_geri_yuklenir()
    {
        var criteria = new SearchCriteria
        {
            Term = "bütçe",
            Range = SearchRange.Past,
            IsRecurring = true,
            IncludeCancelled = true,
        };

        var saved = await _search.SaveAsync(_t.UserId, "Bütçe toplantıları", criteria);
        _t.Detach();

        var loaded = await _search.UseAsync(saved!.Id);

        // Alan alan karşılaştırılır: kayıt eşitliği koleksiyonlara başvuruya
        // göre baktığı için dizi ile liste birbirine eşit çıkmaz.
        Assert.NotNull(loaded);
        Assert.Equal(criteria.Term, loaded.Term);
        Assert.Equal(criteria.Range, loaded.Range);
        Assert.Equal(criteria.IsRecurring, loaded.IsRecurring);
        Assert.Equal(criteria.IncludeCancelled, loaded.IncludeCancelled);
        Assert.Empty(loaded.CalendarIds);
    }

    [Fact]
    public async Task Ayni_ad_uzerine_yazar()
    {
        await _search.SaveAsync(_t.UserId, "Aynı", new SearchCriteria { Term = "ilk" });
        _t.Detach();
        await _search.SaveAsync(_t.UserId, "Aynı", new SearchCriteria { Term = "ikinci" });
        _t.Detach();

        var single = Assert.Single(await _search.GetSavedAsync(_t.UserId));

        Assert.Equal("ikinci", single.Read().Term);
    }

    [Fact]
    public async Task Kayitli_arama_sinirlanir()
    {
        for (var i = 0; i < SearchService.MaxPerUser; i++)
        {
            await _search.SaveAsync(_t.UserId, $"Arama {i}", new SearchCriteria { Term = "x" });
            _t.Detach();
        }

        Assert.Null(await _search.SaveAsync(_t.UserId, "Fazlalık", new SearchCriteria { Term = "x" }));
    }

    [Fact]
    public async Task Kayitli_arama_silinir()
    {
        var saved = await _search.SaveAsync(_t.UserId, "Geçici", new SearchCriteria { Term = "x" });
        _t.Detach();

        await _search.DeleteSavedAsync(saved!.Id);
        _t.Detach();

        Assert.Empty(await _search.GetSavedAsync(_t.UserId));
    }

    [Fact]
    public async Task Cok_kullanilan_arama_ustte()
    {
        await _search.SaveAsync(_t.UserId, "Nadir", new SearchCriteria { Term = "x" });
        _t.Detach();
        var common = await _search.SaveAsync(_t.UserId, "Sık", new SearchCriteria { Term = "y" });
        _t.Detach();

        await _search.UseAsync(common!.Id);
        _t.Detach();

        Assert.Equal("Sık", (await _search.GetSavedAsync(_t.UserId))[0].Name);
    }

    [Fact]
    public async Task Bos_ad_reddedilir()
        => await Assert.ThrowsAsync<ArgumentException>(
            () => _search.SaveAsync(_t.UserId, "  ", new SearchCriteria()));

    [Fact]
    public void Kayan_pencere_kaydedilir_mutlak_tarih_degil()
    {
        // "Gelecek toplantılarım" yarın da gelecek toplantıları göstermeli.
        var criteria = new SearchCriteria { Range = SearchRange.Upcoming };
        var json = SavedSearch.Write(criteria);

        Assert.DoesNotContain("2026", json, StringComparison.Ordinal);
    }
}
