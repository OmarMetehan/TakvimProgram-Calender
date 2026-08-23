using Takvim.Core.Domain;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// Katılımcı ve RSVP akışı. Paylaşımlı modelde tek etkinlik satırı vardır;
/// bir kişinin yanıtı ötekilerde anında görünür.
/// </summary>
public class RsvpTests : IDisposable
{
    private readonly TestDatabase _t = new();
    private readonly Guid _ayse;
    private readonly Guid _mehmet;

    public RsvpTests()
    {
        (_ayse, _) = _t.AddUser("Ayşe Yılmaz", "ayse@ornek.local");
        (_mehmet, _) = _t.AddUser("Mehmet Kaya", "mehmet@ornek.local");
    }

    public void Dispose()
    {
        _t.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>İki davetlisi olan bir toplantı kurar.</summary>
    private async Task<Guid> CreateMeetingAsync(string start = "2026-03-02 10:00", string end = "2026-03-02 11:00")
    {
        var created = await _t.Events.CreateAsync(_t.Input(start, end, "Ekip toplantısı"));
        _t.Detach();

        await _t.Attendees.SyncAsync(created.PrimaryEventId,
        [
            new AttendeeInput("ayse@ornek.local", "Ayşe Yılmaz", _ayse),
            new AttendeeInput("mehmet@ornek.local", "Mehmet Kaya", _mehmet, AttendeeRole.Optional),
        ], _t.UserId);
        _t.Detach();

        return created.PrimaryEventId;
    }

    // ==================================================================
    // Katılımcı listesi
    // ==================================================================

    [Fact]
    public async Task Katilimcilar_eklenir_ve_rol_korunur()
    {
        var eventId = await CreateMeetingAsync();

        var attendees = await _t.Attendees.GetAsync(eventId);

        Assert.Equal(2, attendees.Count);
        Assert.Equal(AttendeeRole.Required, attendees.Single(a => a.UserId == _ayse).Role);
        Assert.Equal(AttendeeRole.Optional, attendees.Single(a => a.UserId == _mehmet).Role);
        Assert.All(attendees, a => Assert.Equal(ResponseStatus.NeedsAction, a.Response));
    }

    [Fact]
    public async Task Listeden_cikarilan_katilimci_silinir()
    {
        var eventId = await CreateMeetingAsync();

        await _t.Attendees.SyncAsync(eventId,
            [new AttendeeInput("ayse@ornek.local", "Ayşe Yılmaz", _ayse)], _t.UserId);
        _t.Detach();

        var attendees = await _t.Attendees.GetAsync(eventId);

        Assert.Single(attendees);
        Assert.Equal(_ayse, attendees[0].UserId);
    }

    [Fact]
    public async Task Liste_yeniden_kaydedilince_verilmis_yanit_korunur()
    {
        var eventId = await CreateMeetingAsync();

        await _t.Attendees.RespondAsync(eventId, _ayse, ResponseStatus.Accepted);
        _t.Detach();

        // Aynı liste yeniden kaydedilir; yalnızca rol değişir.
        await _t.Attendees.SyncAsync(eventId,
        [
            new AttendeeInput("ayse@ornek.local", "Ayşe Yılmaz", _ayse, AttendeeRole.Optional),
            new AttendeeInput("mehmet@ornek.local", "Mehmet Kaya", _mehmet),
        ], _t.UserId);
        _t.Detach();

        var ayse = (await _t.Attendees.GetAsync(eventId)).Single(a => a.UserId == _ayse);

        Assert.Equal(ResponseStatus.Accepted, ayse.Response);
        Assert.Equal(AttendeeRole.Optional, ayse.Role);
    }

    [Fact]
    public async Task Yerel_hesabi_olmayan_kisi_de_davet_edilebilir()
    {
        var created = await _t.Events.CreateAsync(_t.Input("2026-03-02 10:00", "2026-03-02 11:00"));
        _t.Detach();

        await _t.Attendees.SyncAsync(created.PrimaryEventId,
            [new AttendeeInput("dis@baskafirma.com", "Dış Kişi")], _t.UserId);
        _t.Detach();

        var attendee = (await _t.Attendees.GetAsync(created.PrimaryEventId)).Single();

        Assert.Null(attendee.UserId);
        Assert.Equal("dis@baskafirma.com", attendee.Email);
    }

    // ==================================================================
    // Yanıtlar
    // ==================================================================

    [Theory]
    [InlineData(ResponseStatus.Accepted)]
    [InlineData(ResponseStatus.Declined)]
    [InlineData(ResponseStatus.Tentative)]
    public async Task Yanit_kaydedilir(ResponseStatus status)
    {
        var eventId = await CreateMeetingAsync();

        Assert.True(await _t.Attendees.RespondAsync(eventId, _ayse, status, "Notum var"));
        _t.Detach();

        var ayse = (await _t.Attendees.GetAsync(eventId)).Single(a => a.UserId == _ayse);

        Assert.Equal(status, ayse.Response);
        Assert.Equal("Notum var", ayse.ResponseComment);
        Assert.NotNull(ayse.RespondedAt);
    }

    [Fact]
    public async Task Katilim_sekli_kaydedilir()
    {
        var eventId = await CreateMeetingAsync();

        await _t.Attendees.RespondAsync(eventId, _ayse, ResponseStatus.Accepted,
            mode: AttendanceMode.Online);
        _t.Detach();

        var ayse = (await _t.Attendees.GetAsync(eventId)).Single(a => a.UserId == _ayse);

        Assert.Equal(AttendanceMode.Online, ayse.Mode);
    }

    [Fact]
    public async Task Davetli_olmayan_yanit_veremez()
    {
        var eventId = await CreateMeetingAsync();
        var (yabanci, _) = _t.AddUser("Yabancı", "yabanci@ornek.local");

        Assert.False(await _t.Attendees.RespondAsync(eventId, yabanci, ResponseStatus.Accepted));
    }

    [Fact]
    public async Task Yanit_ozeti_dogru_sayar()
    {
        var eventId = await CreateMeetingAsync();

        await _t.Attendees.RespondAsync(eventId, _ayse, ResponseStatus.Accepted);
        _t.Detach();

        var summary = AttendeeService.Summarize(await _t.Attendees.GetAsync(eventId));

        Assert.Equal(1, summary.Accepted);
        Assert.Equal(1, summary.NoResponse);
        Assert.Equal(0, summary.Declined);
        Assert.Equal(2, summary.Total);
    }

    [Fact]
    public async Task Bekleyen_davetler_listelenir()
    {
        var eventId = await CreateMeetingAsync();

        Assert.Single(await _t.Attendees.GetPendingInvitationsAsync(_ayse));

        await _t.Attendees.RespondAsync(eventId, _ayse, ResponseStatus.Accepted);
        _t.Detach();

        Assert.Empty(await _t.Attendees.GetPendingInvitationsAsync(_ayse));
    }

    [Fact]
    public async Task Silinen_etkinligin_daveti_bekleyenlerde_cikmaz()
    {
        var eventId = await CreateMeetingAsync();
        await _t.Events.DeleteAsync(eventId, null, SeriesEditScope.AllInSeries, _t.UserId);
        _t.Detach();

        Assert.Empty(await _t.Attendees.GetPendingInvitationsAsync(_ayse));
    }

    // ==================================================================
    // Yeni zaman önerme
    // ==================================================================

    [Fact]
    public async Task Yeni_zaman_onerisi_kaydedilir_ve_yanit_belirsiz_olur()
    {
        var eventId = await CreateMeetingAsync();

        Assert.True(await _t.Attendees.ProposeNewTimeAsync(eventId, _ayse,
            TestDatabase.Parse("2026-03-02 14:00"), TestDatabase.Parse("2026-03-02 15:00"), "Sabah olmaz"));
        _t.Detach();

        var ayse = (await _t.Attendees.GetAsync(eventId)).Single(a => a.UserId == _ayse);

        Assert.True(ayse.HasProposal);
        Assert.Equal(TestDatabase.Parse("2026-03-02 14:00"), ayse.ProposedStartLocal);
        Assert.Equal("Sabah olmaz", ayse.ProposalNote);
        // Reddetmiyor, başka saat istiyor.
        Assert.Equal(ResponseStatus.Tentative, ayse.Response);
    }

    [Fact]
    public async Task Gecersiz_oneri_reddedilir()
    {
        var eventId = await CreateMeetingAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => _t.Attendees.ProposeNewTimeAsync(
            eventId, _ayse,
            TestDatabase.Parse("2026-03-02 15:00"),
            TestDatabase.Parse("2026-03-02 14:00")));
    }

    [Fact]
    public async Task Oneri_geri_cekilebilir()
    {
        var eventId = await CreateMeetingAsync();

        await _t.Attendees.ProposeNewTimeAsync(eventId, _ayse,
            TestDatabase.Parse("2026-03-02 14:00"), TestDatabase.Parse("2026-03-02 15:00"));
        _t.Detach();

        var attendeeId = (await _t.Attendees.GetAsync(eventId)).Single(a => a.UserId == _ayse).Id;
        await _t.Attendees.WithdrawProposalAsync(attendeeId);
        _t.Detach();

        Assert.False((await _t.Attendees.GetAsync(eventId)).Single(a => a.UserId == _ayse).HasProposal);
    }

    [Fact]
    public async Task Yanit_vermek_bekleyen_oneriyi_temizler()
    {
        var eventId = await CreateMeetingAsync();

        await _t.Attendees.ProposeNewTimeAsync(eventId, _ayse,
            TestDatabase.Parse("2026-03-02 14:00"), TestDatabase.Parse("2026-03-02 15:00"));
        _t.Detach();

        await _t.Attendees.RespondAsync(eventId, _ayse, ResponseStatus.Accepted);
        _t.Detach();

        Assert.False((await _t.Attendees.GetAsync(eventId)).Single(a => a.UserId == _ayse).HasProposal);
    }

    [Fact]
    public async Task Oneri_kabul_edilince_saat_doner_ve_digerleri_sifirlanir()
    {
        var eventId = await CreateMeetingAsync();

        // Mehmet kabul etmiş, Ayşe başka saat öneriyor.
        await _t.Attendees.RespondAsync(eventId, _mehmet, ResponseStatus.Accepted);
        _t.Detach();
        await _t.Attendees.ProposeNewTimeAsync(eventId, _ayse,
            TestDatabase.Parse("2026-03-02 14:00"), TestDatabase.Parse("2026-03-02 15:00"));
        _t.Detach();

        var ayseId = (await _t.Attendees.GetAsync(eventId)).Single(a => a.UserId == _ayse).Id;
        var picked = await _t.Attendees.AcceptProposalAsync(ayseId);
        _t.Detach();

        Assert.NotNull(picked);
        Assert.Equal(TestDatabase.Parse("2026-03-02 14:00"), picked.Value.Start);

        var attendees = await _t.Attendees.GetAsync(eventId);

        // Öneren kabul etmiş sayılır; saat değiştiği için Mehmet'in kabulü düşer.
        Assert.Equal(ResponseStatus.Accepted, attendees.Single(a => a.UserId == _ayse).Response);
        Assert.Equal(ResponseStatus.NeedsAction, attendees.Single(a => a.UserId == _mehmet).Response);
    }

    [Fact]
    public async Task Onerisi_olmayan_katilimci_kabul_edilemez()
    {
        var eventId = await CreateMeetingAsync();
        var attendeeId = (await _t.Attendees.GetAsync(eventId)).Single(a => a.UserId == _ayse).Id;

        Assert.Null(await _t.Attendees.AcceptProposalAsync(attendeeId));
    }

    // ==================================================================
    // Saat değişince yanıtların sıfırlanması
    // ==================================================================

    [Fact]
    public async Task Saat_degisince_yanitlar_sifirlanir()
    {
        var eventId = await CreateMeetingAsync();

        await _t.Attendees.RespondAsync(eventId, _ayse, ResponseStatus.Accepted, "Olur");
        await _t.Attendees.RespondAsync(eventId, _mehmet, ResponseStatus.Declined);
        _t.Detach();

        await _t.Attendees.ResetResponsesAsync(eventId);
        _t.Detach();

        var attendees = await _t.Attendees.GetAsync(eventId);

        Assert.All(attendees, a => Assert.Equal(ResponseStatus.NeedsAction, a.Response));
        Assert.All(attendees, a => Assert.Null(a.ResponseComment));
    }

    // ==================================================================
    // Paylaşımlı model: yanıt herkeste aynı anda görünür
    // ==================================================================

    [Fact]
    public async Task Bir_kisinin_yaniti_digerlerinde_aninda_gorunur()
    {
        // Kopya modeli olsaydı eşitleme gerekirdi; paylaşımlı modelde tek satır var.
        var eventId = await CreateMeetingAsync();

        await _t.Attendees.RespondAsync(eventId, _ayse, ResponseStatus.Accepted);
        _t.Detach();

        var seenByMehmet = await _t.Attendees.GetAsync(eventId);

        Assert.Equal(ResponseStatus.Accepted, seenByMehmet.Single(a => a.UserId == _ayse).Response);
    }

    [Fact]
    public async Task Ayni_kisi_iki_kez_eklenemez()
    {
        var created = await _t.Events.CreateAsync(_t.Input("2026-03-02 10:00", "2026-03-02 11:00"));
        _t.Detach();

        // Aynı e-posta iki kez verilirse tek satır açılır.
        await _t.Attendees.SyncAsync(created.PrimaryEventId,
        [
            new AttendeeInput("ayse@ornek.local", "Ayşe Yılmaz", _ayse),
            new AttendeeInput("AYSE@ornek.local", "Ayşe Y.", _ayse),
        ], _t.UserId);
        _t.Detach();

        Assert.Single(await _t.Attendees.GetAsync(created.PrimaryEventId));
    }
}
