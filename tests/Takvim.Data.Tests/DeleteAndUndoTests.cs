using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

public class DeleteAndUndoTests : IDisposable
{
    private readonly TestDatabase _t = new();

    public void Dispose()
    {
        _t.Dispose();
        GC.SuppressFinalize(this);
    }

    // ==================================================================
    // Silme
    // ==================================================================

    [Fact]
    public async Task Tekil_etkinlik_silinince_cop_kutusuna_gider()
    {
        var created = await _t.Events.CreateAsync(_t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Tekil"));
        _t.Detach();

        await _t.Events.DeleteAsync(created.PrimaryEventId, null, SeriesEditScope.AllInSeries, _t.UserId);
        _t.Detach();

        Assert.Empty(await _t.StartsAsync("2026-03-01 00:00", "2026-03-05 00:00"));

        var trash = await _t.Query.GetTrashAsync(_t.UserId);
        Assert.Single(trash);
        Assert.Equal("Tekil", trash[0].Title);
        // Satır silinmez, yalnızca işaretlenir.
        Assert.NotNull(await _t.Db.Events.FirstOrDefaultAsync(e => e.Id == created.PrimaryEventId));
    }

    [Fact]
    public async Task Tek_ornek_silince_seri_koküne_exdate_eklenir()
    {
        var created = await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Toplantı", "FREQ=DAILY"));
        _t.Detach();

        await _t.Events.DeleteAsync(created.PrimaryEventId, TestDatabase.Parse("2026-03-03 09:00"),
            SeriesEditScope.ThisOnly, _t.UserId);
        _t.Detach();

        var root = await _t.Db.Events.SingleAsync();

        Assert.Equal("2026-03-03T09:00:00", root.ExDates);
        Assert.Equal(
            ["2026-03-02 09:00", "2026-03-04 09:00"],
            await _t.StartsAsync("2026-03-02 00:00", "2026-03-05 00:00"));
    }

    [Fact]
    public async Task Bu_ve_sonrakileri_silmek_seriyi_keser()
    {
        var created = await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Toplantı", "FREQ=DAILY"));
        _t.Detach();

        await _t.Events.DeleteAsync(created.PrimaryEventId, TestDatabase.Parse("2026-03-04 09:00"),
            SeriesEditScope.ThisAndFuture, _t.UserId);
        _t.Detach();

        Assert.Equal(
            ["2026-03-02 09:00", "2026-03-03 09:00"],
            await _t.StartsAsync("2026-03-01 00:00", "2026-04-01 00:00"));
    }

    [Fact]
    public async Task Tum_seriyi_silmek_istisnalari_da_siler()
    {
        var created = await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Toplantı", "FREQ=DAILY"));
        _t.Detach();

        await _t.Events.UpdateAsync(created.PrimaryEventId, TestDatabase.Parse("2026-03-03 09:00"),
            SeriesEditScope.ThisOnly, _t.Input("2026-03-03 14:00", "2026-03-03 15:00", "Taşınmış"));
        _t.Detach();

        await _t.Events.DeleteAsync(created.PrimaryEventId, null, SeriesEditScope.AllInSeries, _t.UserId);
        _t.Detach();

        Assert.Empty(await _t.StartsAsync("2026-03-01 00:00", "2026-04-01 00:00"));
        Assert.Equal(2, await _t.Db.Events.CountAsync(e => e.DeletedAt != null));
    }

    [Fact]
    public async Task Istisna_satirindan_silmek_seri_kokune_yonlendirir()
    {
        // Kullanıcı taşınmış bir örneğe tıklayıp "tüm seriyi sil" derse,
        // istisna satırından seri köküne çıkılmalıdır.
        var created = await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Toplantı", "FREQ=DAILY"));
        _t.Detach();

        await _t.Events.UpdateAsync(created.PrimaryEventId, TestDatabase.Parse("2026-03-03 09:00"),
            SeriesEditScope.ThisOnly, _t.Input("2026-03-03 14:00", "2026-03-03 15:00", "Taşınmış"));
        _t.Detach();

        var exceptionId = (await _t.Db.Events.SingleAsync(e => e.RecurrenceId != null)).Id;
        await _t.Events.DeleteAsync(exceptionId, null, SeriesEditScope.AllInSeries, _t.UserId);
        _t.Detach();

        Assert.Empty(await _t.StartsAsync("2026-03-01 00:00", "2026-04-01 00:00"));
    }

    // ==================================================================
    // Çöp kutusu
    // ==================================================================

    [Fact]
    public async Task Cop_kutusundan_geri_alinan_etkinlik_yeniden_gorunur()
    {
        var created = await _t.Events.CreateAsync(_t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Tekil"));
        _t.Detach();
        await _t.Events.DeleteAsync(created.PrimaryEventId, null, SeriesEditScope.AllInSeries, _t.UserId);
        _t.Detach();

        await _t.Events.RestoreAsync(created.PrimaryEventId, _t.UserId);
        _t.Detach();

        Assert.Equal(["2026-03-02 09:00"], await _t.StartsAsync("2026-03-01 00:00", "2026-03-05 00:00"));
        Assert.Empty(await _t.Query.GetTrashAsync(_t.UserId));
    }

    [Fact]
    public async Task Otuz_gunu_gecen_kayitlar_kalici_silinir()
    {
        var created = await _t.Events.CreateAsync(_t.Input("2026-03-02 09:00", "2026-03-02 10:00"));
        _t.Detach();
        await _t.Events.DeleteAsync(created.PrimaryEventId, null, SeriesEditScope.AllInSeries, _t.UserId);
        _t.Detach();

        // 29 gün sonra hâlâ durur.
        _t.Clock.Advance(Duration.FromDays(29));
        Assert.Equal(0, await _t.Events.PurgeTrashAsync());
        Assert.Single(await _t.Query.GetTrashAsync(_t.UserId));

        // 31 gün sonra temizlenir.
        _t.Clock.Advance(Duration.FromDays(2));
        Assert.Equal(1, await _t.Events.PurgeTrashAsync());
        _t.Detach();
        Assert.Empty(await _t.Query.GetTrashAsync(_t.UserId));
    }

    // ==================================================================
    // Geri alma
    // ==================================================================

    [Fact]
    public async Task Olusturma_geri_alininca_etkinlik_kaybolur()
    {
        var created = await _t.Events.CreateAsync(_t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Yanlışlıkla"));
        _t.Detach();

        Assert.True(await _t.Undo.UndoAsync(created.UndoToken, _t.UserId));
        _t.Detach();

        Assert.Empty(await _t.StartsAsync("2026-03-01 00:00", "2026-03-05 00:00"));
        Assert.Empty(await _t.Db.Events.Where(e => e.DeletedAt == null).ToListAsync());
    }

    [Fact]
    public async Task Silme_geri_alininca_etkinlik_geri_gelir()
    {
        var created = await _t.Events.CreateAsync(_t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Önemli"));
        _t.Detach();

        var deleted = await _t.Events.DeleteAsync(created.PrimaryEventId, null, SeriesEditScope.AllInSeries, _t.UserId);
        _t.Detach();

        Assert.True(await _t.Undo.UndoAsync(deleted.UndoToken, _t.UserId));
        _t.Detach();

        Assert.Equal(["2026-03-02 09:00"], await _t.StartsAsync("2026-03-01 00:00", "2026-03-05 00:00"));
        Assert.Equal("Önemli", (await _t.TitlesAsync("2026-03-01 00:00", "2026-03-05 00:00"))[0]);
    }

    [Fact]
    public async Task Tasima_geri_alininca_eski_saate_doner()
    {
        var created = await _t.Events.CreateAsync(_t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Toplantı"));
        _t.Detach();

        var moved = await _t.Events.UpdateAsync(created.PrimaryEventId, null, SeriesEditScope.AllInSeries,
            _t.Input("2026-03-02 15:00", "2026-03-02 16:00", "Toplantı"));
        _t.Detach();

        Assert.Equal(["2026-03-02 15:00"], await _t.StartsAsync("2026-03-01 00:00", "2026-03-05 00:00"));

        Assert.True(await _t.Undo.UndoAsync(moved.UndoToken, _t.UserId));
        _t.Detach();

        Assert.Equal(["2026-03-02 09:00"], await _t.StartsAsync("2026-03-01 00:00", "2026-03-05 00:00"));
    }

    [Fact]
    public async Task Seri_bolmesi_tek_hamlede_geri_alinir()
    {
        // Bu işlem iki satıra dokunur: eski seri kesilir, yeni seri açılır.
        // Geri alma ikisini birlikte çevirmelidir.
        var created = await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Eski", "FREQ=DAILY"));
        _t.Detach();

        var split = await _t.Events.UpdateAsync(created.PrimaryEventId, TestDatabase.Parse("2026-03-04 09:00"),
            SeriesEditScope.ThisAndFuture, _t.Input("2026-03-04 11:00", "2026-03-04 12:00", "Yeni", "FREQ=DAILY"));
        _t.Detach();

        Assert.Equal(2, await _t.Db.Events.CountAsync(e => e.SeriesId == null && e.DeletedAt == null));

        Assert.True(await _t.Undo.UndoAsync(split.UndoToken, _t.UserId));
        _t.Detach();

        Assert.Equal(1, await _t.Db.Events.CountAsync(e => e.SeriesId == null && e.DeletedAt == null));
        Assert.Equal(
            ["2026-03-02 09:00", "2026-03-03 09:00", "2026-03-04 09:00", "2026-03-05 09:00"],
            await _t.StartsAsync("2026-03-02 00:00", "2026-03-06 00:00"));
    }

    [Fact]
    public async Task Geri_alinabilir_islemler_listelenir()
    {
        await _t.Events.CreateAsync(_t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Birinci"));
        _t.Detach();
        await _t.Events.CreateAsync(_t.Input("2026-03-03 09:00", "2026-03-03 10:00", "İkinci"));
        _t.Detach();

        var recent = await _t.Undo.GetRecentAsync();

        Assert.Equal(2, recent.Count);
        Assert.Contains("İkinci", recent[0].Description, StringComparison.Ordinal);
        Assert.Contains("Birinci", recent[1].Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bilinmeyen_islem_geri_alinamaz()
        => Assert.False(await _t.Undo.UndoAsync(Guid.NewGuid(), _t.UserId));

    // ==================================================================
    // Denetim kaydı
    // ==================================================================

    [Fact]
    public async Task Her_degisiklik_gunluge_yazilir()
    {
        var created = await _t.Events.CreateAsync(_t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Toplantı"));
        _t.Detach();
        await _t.Events.UpdateAsync(created.PrimaryEventId, null, SeriesEditScope.AllInSeries,
            _t.Input("2026-03-02 11:00", "2026-03-02 12:00", "Toplantı"));
        _t.Detach();
        await _t.Events.DeleteAsync(created.PrimaryEventId, null, SeriesEditScope.AllInSeries, _t.UserId);
        _t.Detach();

        var log = await _t.Db.ChangeLog.OrderBy(c => c.SyncToken).ToListAsync();

        Assert.Equal(
            [ChangeOperation.Create, ChangeOperation.Update, ChangeOperation.Delete],
            log.Select(c => c.Operation));
        Assert.All(log, c => Assert.Equal(_t.UserId, c.ActorUserId));
        Assert.All(log, c => Assert.NotEqual(Guid.Empty, c.OperationId));

        // Senkronizasyon imleci artan olmalıdır.
        Assert.Equal(log.Select(c => c.SyncToken).OrderBy(t => t), log.Select(c => c.SyncToken));
    }

    [Fact]
    public async Task Gunlukteki_onceki_hal_geri_almayi_besler()
    {
        var created = await _t.Events.CreateAsync(_t.Input("2026-03-02 09:00", "2026-03-02 10:00", "İlk başlık"));
        _t.Detach();
        await _t.Events.UpdateAsync(created.PrimaryEventId, null, SeriesEditScope.AllInSeries,
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Yeni başlık"));
        _t.Detach();

        var update = await _t.Db.ChangeLog.SingleAsync(c => c.Operation == ChangeOperation.Update);
        var before = ChangeLogWriter.Deserialize(update.BeforeJson);

        Assert.NotNull(before);
        Assert.Equal("İlk başlık", before.Title);
        Assert.Contains("İlk başlık", update.Summary, StringComparison.Ordinal);
    }
}
