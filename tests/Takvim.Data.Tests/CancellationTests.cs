using Microsoft.EntityFrameworkCore;
using Takvim.Core.Domain;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// Toplantı iptali. Silmekten farkı: kayıt durur, katılımcılar toplantının
/// neden olmadığını görür. Silmek bu bilgiyi yok ederdi.
/// </summary>
public class CancellationTests : IDisposable
{
    private readonly TestDatabase _t = new();
    private readonly Guid _ayse;

    public CancellationTests()
        => (_ayse, _) = _t.AddUser("Ayşe Yılmaz", "ayse@ornek.local");

    public void Dispose()
    {
        _t.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<Guid> CreateMeetingAsync()
    {
        var created = await _t.Events.CreateAsync(
            _t.Input("2026-03-02 10:00", "2026-03-02 11:00", "Ekip toplantısı"));
        _t.Detach();

        await _t.Attendees.SyncAsync(created.PrimaryEventId,
            [new AttendeeInput("ayse@ornek.local", "Ayşe Yılmaz", _ayse)], _t.UserId);
        _t.Detach();

        return created.PrimaryEventId;
    }

    // ==================================================================

    [Fact]
    public async Task Iptal_kaydi_silmez()
    {
        var eventId = await CreateMeetingAsync();

        await _t.Events.CancelMeetingAsync(eventId, "Salon müsait değil", _t.UserId);
        _t.Detach();

        var ev = await _t.Db.Events.SingleAsync();

        Assert.Equal(EventStatus.Cancelled, ev.Status);
        Assert.Equal("Salon müsait değil", ev.CancellationReason);
        // Çöp kutusuna gitmez; katılımcılar görmeye devam etmeli.
        Assert.Null(ev.DeletedAt);
    }

    [Fact]
    public async Task Gerekce_zorunlu_degil()
    {
        var eventId = await CreateMeetingAsync();

        await _t.Events.CancelMeetingAsync(eventId, null, _t.UserId);
        _t.Detach();

        var ev = await _t.Db.Events.SingleAsync();

        Assert.Equal(EventStatus.Cancelled, ev.Status);
        Assert.Null(ev.CancellationReason);
    }

    [Fact]
    public async Task Bos_gerekce_null_olarak_saklanir()
    {
        var eventId = await CreateMeetingAsync();

        await _t.Events.CancelMeetingAsync(eventId, "   ", _t.UserId);
        _t.Detach();

        Assert.Null((await _t.Db.Events.SingleAsync()).CancellationReason);
    }

    [Fact]
    public async Task Iptal_denetim_kaydina_gerekceyle_yazilir()
    {
        var eventId = await CreateMeetingAsync();

        await _t.Events.CancelMeetingAsync(eventId, "Salon müsait değil", _t.UserId);
        _t.Detach();

        var entry = await _t.Db.ChangeLog
            .AsNoTracking()
            .Where(c => c.Summary != null && c.Summary.Contains("iptal"))
            .SingleAsync();

        Assert.Contains("Salon müsait değil", entry.Summary, StringComparison.Ordinal);
        Assert.Equal(_t.UserId, entry.ActorUserId);
    }

    [Fact]
    public async Task Iptal_edilen_toplanti_izgarada_gorunmez()
    {
        // Varsayılan süzgeç iptal edilenleri elemeli; kullanıcı isterse gösterilir.
        var eventId = await CreateMeetingAsync();
        await _t.Events.CancelMeetingAsync(eventId, "Ertelendi", _t.UserId);
        _t.Detach();

        Assert.Empty(await _t.StartsAsync("2026-03-01 00:00", "2026-03-05 00:00"));

        var withCancelled = await _t.Query.GetOccurrencesAsync(
            _t.UserId, _t.Utc("2026-03-01 00:00"), _t.Utc("2026-03-05 00:00"),
            new OccurrenceFilter { IncludeCancelled = true });

        Assert.Single(withCancelled);
        Assert.Equal("Ertelendi", withCancelled[0].Source.CancellationReason);
    }

    [Fact]
    public async Task Iptal_geri_alinabilir()
    {
        var eventId = await CreateMeetingAsync();

        var result = await _t.Events.CancelMeetingAsync(eventId, "Yanlışlıkla", _t.UserId);
        _t.Detach();

        Assert.True(await _t.Undo.UndoAsync(result.UndoToken, _t.UserId));
        _t.Detach();

        var ev = await _t.Db.Events.SingleAsync();

        Assert.Equal(EventStatus.Confirmed, ev.Status);
        Assert.Null(ev.CancellationReason);
    }

    [Fact]
    public async Task Iptal_katilimcilari_silmez()
    {
        var eventId = await CreateMeetingAsync();

        await _t.Events.CancelMeetingAsync(eventId, "Ertelendi", _t.UserId);
        _t.Detach();

        // Katılımcılar durur; kimin haberdar edilmesi gerektiği bilinmeli.
        Assert.Single(await _t.Attendees.GetAsync(eventId));
    }

    [Fact]
    public async Task Olmayan_etkinlik_iptal_edilemez()
        => await Assert.ThrowsAsync<InvalidOperationException>(
            () => _t.Events.CancelMeetingAsync(Guid.NewGuid(), null, _t.UserId));

    // ==================================================================
    // Katılımcı üzerinde arama
    // ==================================================================

    [Fact]
    public async Task Katilimci_adiyla_arama_calisir()
    {
        await CreateMeetingAsync();

        var found = await _t.Query.GetOccurrencesAsync(
            _t.UserId, _t.Utc("2026-03-01 00:00"), _t.Utc("2026-03-05 00:00"),
            new OccurrenceFilter { SearchTerm = "Ayşe" });

        Assert.Single(found);
    }

    [Fact]
    public async Task Katilimci_epostasiyla_arama_calisir()
    {
        await CreateMeetingAsync();

        var found = await _t.Query.GetOccurrencesAsync(
            _t.UserId, _t.Utc("2026-03-01 00:00"), _t.Utc("2026-03-05 00:00"),
            new OccurrenceFilter { SearchTerm = "ayse@ornek" });

        Assert.Single(found);
    }

    [Fact]
    public async Task Katilimci_cikarilinca_aramadan_da_duser()
    {
        var eventId = await CreateMeetingAsync();

        await _t.Attendees.SyncAsync(eventId, [], _t.UserId);
        _t.Detach();

        var found = await _t.Query.GetOccurrencesAsync(
            _t.UserId, _t.Utc("2026-03-01 00:00"), _t.Utc("2026-03-05 00:00"),
            new OccurrenceFilter { SearchTerm = "Ayşe" });

        Assert.Empty(found);
    }

    [Fact]
    public async Task Baslikla_arama_katilimci_eklendikten_sonra_da_calisir()
    {
        // Arama metni yeniden kurulurken başlık kaybolmamalı.
        await CreateMeetingAsync();

        var found = await _t.Query.GetOccurrencesAsync(
            _t.UserId, _t.Utc("2026-03-01 00:00"), _t.Utc("2026-03-05 00:00"),
            new OccurrenceFilter { SearchTerm = "ekip" });

        Assert.Single(found);
    }
}
