using Microsoft.EntityFrameworkCore;
using Takvim.Core.Permissions;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// Özel notlar detay seviyesinden bağımsız bir kısıttır: etkinliğin tüm
/// detayını gören biri bile organizatörün kendine aldığı notu görmemelidir.
/// Bu yüzden ayrı sınanır — izin tablosunun geri kalanına benzemez.
/// </summary>
public class PrivateNotesTests : IDisposable
{
    private readonly TestDatabase _t = new();

    public void Dispose()
    {
        _t.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task CreateWithNotesAsync()
    {
        var input = _t.Input("2026-03-02 10:00", "2026-03-02 11:00") with
        {
            AgendaText = "1. Açılış",
            PrivateNotes = "Bütçeyi açma",
        };

        await _t.Events.CreateAsync(input);
        _t.Detach();
    }

    private async Task<string?> NotesSeenByAsync(Guid userId)
    {
        var occurrences = await _t.SeenByAsync(userId, "2026-03-02 00:00", "2026-03-03 00:00");
        return occurrences.Single().Source.PrivateNotes;
    }

    private async Task<string?> AgendaSeenByAsync(Guid userId)
    {
        var occurrences = await _t.SeenByAsync(userId, "2026-03-02 00:00", "2026-03-03 00:00");
        return occurrences.Single().Source.AgendaText;
    }

    [Fact]
    public async Task Sahibi_notunu_gorur()
    {
        await CreateWithNotesAsync();

        Assert.Equal("Bütçeyi açma", await NotesSeenByAsync(_t.UserId));
    }

    [Fact]
    public async Task Tum_detayi_goren_paylasim_notu_gormez()
    {
        await CreateWithNotesAsync();

        var (otherUserId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");
        _t.Share(_t.CalendarId, otherUserId, SharingLevel.FullDetails);

        // Gündemi görür — detay seviyesindedir.
        Assert.Equal("1. Açılış", await AgendaSeenByAsync(otherUserId));

        // Notu görmez.
        Assert.Null(await NotesSeenByAsync(otherUserId));
    }

    [Fact]
    public async Task Duzenleyebilen_paylasim_notu_gorur()
    {
        await CreateWithNotesAsync();

        var (otherUserId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");
        _t.Share(_t.CalendarId, otherUserId, SharingLevel.CanEdit);

        // Alanı zaten değiştirebilecek durumda; gizlemenin anlamı olmazdı.
        Assert.Equal("Bütçeyi açma", await NotesSeenByAsync(otherUserId));
    }

    [Fact]
    public async Task Baslik_seviyesindeki_paylasim_ne_gundem_ne_not_gorur()
    {
        await CreateWithNotesAsync();

        var (otherUserId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");
        _t.Share(_t.CalendarId, otherUserId, SharingLevel.TitleLocation);

        Assert.Null(await AgendaSeenByAsync(otherUserId));
        Assert.Null(await NotesSeenByAsync(otherUserId));
    }

    [Fact]
    public async Task Not_gizlenirken_kaynak_kayit_bozulmaz()
    {
        await CreateWithNotesAsync();

        var (otherUserId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");
        _t.Share(_t.CalendarId, otherUserId, SharingLevel.FullDetails);

        Assert.Null(await NotesSeenByAsync(otherUserId));
        _t.Detach();

        // Gizleme kopya üzerinde yapılır; veritabanındaki satır dokunulmadan kalır.
        Assert.Equal("Bütçeyi açma", await NotesSeenByAsync(_t.UserId));
    }

    [Fact]
    public async Task Notu_olmayan_etkinlik_kopyalanmaz()
    {
        // Kopya almanın maliyeti boşuna ödenmemeli: alan null ise kaynak
        // doğrudan geçer.
        await _t.Events.CreateAsync(_t.Input("2026-03-02 10:00", "2026-03-02 11:00"));
        _t.Detach();

        var (otherUserId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");
        _t.Share(_t.CalendarId, otherUserId, SharingLevel.FullDetails);

        var mine = (await _t.SeenByAsync(_t.UserId, "2026-03-02 00:00", "2026-03-03 00:00")).Single();
        var theirs = (await _t.SeenByAsync(otherUserId, "2026-03-02 00:00", "2026-03-03 00:00")).Single();

        Assert.Equal(mine.EventId, theirs.EventId);
        Assert.Null(theirs.Source.PrivateNotes);
    }

    [Fact]
    public async Task Not_disa_aktarmaya_girmez()
    {
        await CreateWithNotesAsync();

        var events = await _t.Db.Events.ToListAsync();
        var ics = new Takvim.Core.Ics.IcsSerializer(_t.Zones).Export(events, "Kişisel");

        Assert.Contains("X-TAKVIM-AGENDA", ics, StringComparison.Ordinal);
        Assert.DoesNotContain("Bütçeyi açma", ics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gundem_aramaya_girer()
    {
        await CreateWithNotesAsync();

        var found = await _t.Query.SearchAsync(
            _t.UserId, "açılış", _t.Utc("2026-01-01 00:00"), _t.Utc("2027-01-01 00:00"));

        Assert.Single(found);
    }
}
