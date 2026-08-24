using Microsoft.EntityFrameworkCore;
using Takvim.Core.Domain;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// Toplantı taşındığında yanıtlara ne olduğu.
/// <para>
/// Karar: yanıt silinmez, "hangi saate verildiği" kaydedilir ve toplantı
/// taşınınca eski olarak işaretlenir. Gerekçe: "Katılacak (eski saate göre)",
/// "yanıt yok"tan daha fazlasını söyler — organizatör kimin hevesli olduğunu
/// ve kime ayrıca sorması gerektiğini ayırt edebilir.
/// </para>
/// </summary>
public class StaleResponseTests : IDisposable
{
    private readonly TestDatabase _t = new();
    private readonly Guid _ayse;
    private readonly Guid _mehmet;

    public StaleResponseTests()
    {
        (_ayse, _) = _t.AddUser("Ayşe Yılmaz", "ayse@ornek.local");
        (_mehmet, _) = _t.AddUser("Mehmet Kaya", "mehmet@ornek.local");
    }

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
        [
            new AttendeeInput("ayse@ornek.local", "Ayşe Yılmaz", _ayse),
            new AttendeeInput("mehmet@ornek.local", "Mehmet Kaya", _mehmet),
        ], _t.UserId);
        _t.Detach();

        return created.PrimaryEventId;
    }

    /// <summary>Toplantıyı yeni bir saate taşır.</summary>
    private async Task MoveAsync(Guid eventId, string start, string end)
    {
        await _t.Events.UpdateAsync(eventId, null, SeriesEditScope.AllInSeries,
            _t.Input(start, end, "Ekip toplantısı"));
        _t.Detach();
    }

    private async Task<Attendee> AttendeeAsync(Guid eventId, Guid userId)
        => (await _t.Attendees.GetAsync(eventId)).Single(a => a.UserId == userId);

    // ==================================================================
    // Yanıtın verildiği saatin kaydı
    // ==================================================================

    [Fact]
    public async Task Yanit_verilirken_toplantinin_saati_kaydedilir()
    {
        var eventId = await CreateMeetingAsync();

        await _t.Attendees.RespondAsync(eventId, _ayse, ResponseStatus.Accepted);
        _t.Detach();

        var ayse = await AttendeeAsync(eventId, _ayse);

        Assert.Equal(TestDatabase.Parse("2026-03-02 10:00"), ayse.RespondedForStartLocal);
        Assert.False(ayse.IsResponseStale);
    }

    [Fact]
    public async Task Yanit_verilmemisse_eski_sayilmaz()
    {
        var eventId = await CreateMeetingAsync();
        await MoveAsync(eventId, "2026-03-02 14:00", "2026-03-02 15:00");

        var ayse = await AttendeeAsync(eventId, _ayse);

        Assert.Equal(ResponseStatus.NeedsAction, ayse.Response);
        Assert.False(ayse.IsResponseStale);
    }

    // ==================================================================
    // Taşınma
    // ==================================================================

    [Fact]
    public async Task Toplanti_tasininca_yanit_silinmez()
    {
        var eventId = await CreateMeetingAsync();

        await _t.Attendees.RespondAsync(eventId, _ayse, ResponseStatus.Accepted, "Olur");
        _t.Detach();

        await MoveAsync(eventId, "2026-03-02 14:00", "2026-03-02 15:00");

        var ayse = await AttendeeAsync(eventId, _ayse);

        // Yanıt duruyor, notu da duruyor.
        Assert.Equal(ResponseStatus.Accepted, ayse.Response);
        Assert.Equal("Olur", ayse.ResponseComment);
    }

    [Fact]
    public async Task Toplanti_tasininca_yanit_eski_isaretlenir()
    {
        var eventId = await CreateMeetingAsync();

        await _t.Attendees.RespondAsync(eventId, _ayse, ResponseStatus.Accepted);
        _t.Detach();

        await MoveAsync(eventId, "2026-03-02 14:00", "2026-03-02 15:00");

        var ayse = await AttendeeAsync(eventId, _ayse);

        Assert.True(ayse.IsResponseStale);
        // Hangi saate verildiği hâlâ okunabilir.
        Assert.Equal(TestDatabase.Parse("2026-03-02 10:00"), ayse.RespondedForStartLocal);
    }

    [Fact]
    public async Task Baska_gune_tasima_da_eskitir()
    {
        var eventId = await CreateMeetingAsync();

        await _t.Attendees.RespondAsync(eventId, _ayse, ResponseStatus.Accepted);
        _t.Detach();

        await MoveAsync(eventId, "2026-03-09 10:00", "2026-03-09 11:00");

        Assert.True((await AttendeeAsync(eventId, _ayse)).IsResponseStale);
    }

    [Fact]
    public async Task Saat_degismeyen_duzenleme_yaniti_eskitmez()
    {
        var eventId = await CreateMeetingAsync();

        await _t.Attendees.RespondAsync(eventId, _ayse, ResponseStatus.Accepted);
        _t.Detach();

        // Yalnızca başlık değişiyor; saat aynı.
        await _t.Events.UpdateAsync(eventId, null, SeriesEditScope.AllInSeries,
            _t.Input("2026-03-02 10:00", "2026-03-02 11:00", "Yeni başlık"));
        _t.Detach();

        Assert.False((await AttendeeAsync(eventId, _ayse)).IsResponseStale);
    }

    [Fact]
    public async Task Eski_yanit_guncellenince_isaret_kalkar()
    {
        var eventId = await CreateMeetingAsync();

        await _t.Attendees.RespondAsync(eventId, _ayse, ResponseStatus.Accepted);
        _t.Detach();
        await MoveAsync(eventId, "2026-03-02 14:00", "2026-03-02 15:00");

        Assert.True((await AttendeeAsync(eventId, _ayse)).IsResponseStale);

        // Ayşe yeni saate göre yeniden yanıtlıyor.
        await _t.Attendees.RespondAsync(eventId, _ayse, ResponseStatus.Accepted);
        _t.Detach();

        var ayse = await AttendeeAsync(eventId, _ayse);

        Assert.False(ayse.IsResponseStale);
        Assert.Equal(TestDatabase.Parse("2026-03-02 14:00"), ayse.RespondedForStartLocal);
    }

    // ==================================================================
    // Özet sayacı
    // ==================================================================

    [Fact]
    public async Task Ozet_eski_yanitlari_ayrica_sayar()
    {
        var eventId = await CreateMeetingAsync();

        await _t.Attendees.RespondAsync(eventId, _ayse, ResponseStatus.Accepted);
        _t.Detach();
        await MoveAsync(eventId, "2026-03-02 14:00", "2026-03-02 15:00");

        // Mehmet yeni saate göre yanıtlıyor.
        await _t.Attendees.RespondAsync(eventId, _mehmet, ResponseStatus.Accepted);
        _t.Detach();

        var summary = AttendeeService.Summarize(await _t.Attendees.GetAsync(eventId));

        Assert.Equal(2, summary.Accepted);
        Assert.Equal(1, summary.Stale);
        Assert.Contains("1 yanıt eski saate göre", summary.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Eski_yanit_yoksa_ozette_gecmez()
    {
        var eventId = await CreateMeetingAsync();

        await _t.Attendees.RespondAsync(eventId, _ayse, ResponseStatus.Accepted);
        _t.Detach();

        var summary = AttendeeService.Summarize(await _t.Attendees.GetAsync(eventId));

        Assert.Equal(0, summary.Stale);
        Assert.DoesNotContain("eski saate", summary.Text, StringComparison.Ordinal);
    }

    // ==================================================================
    // Zaman önerisi kabulü
    // ==================================================================

    [Fact]
    public async Task Oneri_kabul_edilince_digerlerinin_yaniti_silinmez()
    {
        // Eski davranış bunları sıfırlıyordu; artık korunup eskitiliyorlar.
        var eventId = await CreateMeetingAsync();

        await _t.Attendees.RespondAsync(eventId, _mehmet, ResponseStatus.Accepted);
        _t.Detach();
        await _t.Attendees.ProposeNewTimeAsync(eventId, _ayse,
            TestDatabase.Parse("2026-03-02 14:00"), TestDatabase.Parse("2026-03-02 15:00"));
        _t.Detach();

        var ayseId = (await AttendeeAsync(eventId, _ayse)).Id;
        var picked = await _t.Attendees.AcceptProposalAsync(ayseId);
        _t.Detach();

        Assert.NotNull(picked);

        // Organizatör etkinliği önerilen saate taşır.
        await MoveAsync(eventId, "2026-03-02 14:00", "2026-03-02 15:00");

        var mehmet = await AttendeeAsync(eventId, _mehmet);

        Assert.Equal(ResponseStatus.Accepted, mehmet.Response);
        Assert.True(mehmet.IsResponseStale);
    }

    [Fact]
    public async Task Oneren_kisi_yeni_saati_kabul_etmis_sayilir()
    {
        var eventId = await CreateMeetingAsync();

        await _t.Attendees.ProposeNewTimeAsync(eventId, _ayse,
            TestDatabase.Parse("2026-03-02 14:00"), TestDatabase.Parse("2026-03-02 15:00"));
        _t.Detach();

        var ayseId = (await AttendeeAsync(eventId, _ayse)).Id;
        await _t.Attendees.AcceptProposalAsync(ayseId);
        _t.Detach();

        await MoveAsync(eventId, "2026-03-02 14:00", "2026-03-02 15:00");

        var ayse = await AttendeeAsync(eventId, _ayse);

        Assert.Equal(ResponseStatus.Accepted, ayse.Response);
        // Yanıtı yeni saate verilmiş sayıldığı için eski değil.
        Assert.False(ayse.IsResponseStale);
    }

    // ==================================================================
    // Elle sıfırlama
    // ==================================================================

    [Fact]
    public async Task Elle_sifirlama_yanitlari_ve_isaretleri_temizler()
    {
        var eventId = await CreateMeetingAsync();

        await _t.Attendees.RespondAsync(eventId, _ayse, ResponseStatus.Accepted, "Olur");
        _t.Detach();
        await MoveAsync(eventId, "2026-03-02 14:00", "2026-03-02 15:00");

        await _t.Attendees.ResetResponsesAsync(eventId);
        _t.Detach();

        var ayse = await AttendeeAsync(eventId, _ayse);

        Assert.Equal(ResponseStatus.NeedsAction, ayse.Response);
        Assert.Null(ayse.ResponseComment);
        Assert.Null(ayse.RespondedForStartLocal);
        Assert.False(ayse.IsResponseStale);
    }

    // ==================================================================
    // Güvenli varsayılan
    // ==================================================================

    [Fact]
    public async Task Etkinlik_yuklenmemisse_eski_isareti_verilmez()
    {
        // Yanlış uyarı vermektense sessiz kalmak yeğdir.
        var eventId = await CreateMeetingAsync();

        await _t.Attendees.RespondAsync(eventId, _ayse, ResponseStatus.Accepted);
        _t.Detach();
        await MoveAsync(eventId, "2026-03-02 14:00", "2026-03-02 15:00");

        var withoutEvent = await _t.Db.Attendees
            .AsNoTracking()
            .FirstAsync(a => a.EventId == eventId && a.UserId == _ayse);

        Assert.Null(withoutEvent.Event);
        Assert.False(withoutEvent.IsResponseStale);
    }
}
