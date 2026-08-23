using Microsoft.EntityFrameworkCore;
using Takvim.Core.Domain;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// Seri düzenleme kapsamlarının davranışı. Bu kurallar takvim uygulamalarında
/// en çok veri kaybına yol açan yerdir; her kapsamın hem etkilediği hem de
/// <b>etkilemediği</b> örnekler doğrulanır.
/// </summary>
public class SeriesEditTests : IDisposable
{
    private readonly TestDatabase _t = new();

    public void Dispose()
    {
        _t.Dispose();
        GC.SuppressFinalize(this);
    }

    // ==================================================================
    // Bu etkinlik (ThisOnly)
    // ==================================================================

    [Fact]
    public async Task Tek_ornek_duzenlemesi_yalnizca_o_ornegi_degistirir()
    {
        var created = await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Ekip toplantısı", "FREQ=DAILY"));
        _t.Detach();

        await _t.Events.UpdateAsync(
            created.PrimaryEventId,
            TestDatabase.Parse("2026-03-03 09:00"),
            SeriesEditScope.ThisOnly,
            _t.Input("2026-03-03 14:00", "2026-03-03 15:00", "Ekip toplantısı (ertelendi)"));
        _t.Detach();

        Assert.Equal(
            ["2026-03-02 09:00", "2026-03-03 14:00", "2026-03-04 09:00"],
            await _t.StartsAsync("2026-03-02 00:00", "2026-03-05 00:00"));

        Assert.Equal(
            ["Ekip toplantısı", "Ekip toplantısı (ertelendi)", "Ekip toplantısı"],
            await _t.TitlesAsync("2026-03-02 00:00", "2026-03-05 00:00"));
    }

    [Fact]
    public async Task Tek_ornek_istisnasi_seriyle_ayni_uid_tasir()
    {
        var created = await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Toplantı", "FREQ=DAILY"));
        var rootUid = (await _t.Db.Events.SingleAsync()).Uid;
        _t.Detach();

        await _t.Events.UpdateAsync(created.PrimaryEventId, TestDatabase.Parse("2026-03-03 09:00"),
            SeriesEditScope.ThisOnly, _t.Input("2026-03-03 14:00", "2026-03-03 15:00"));
        _t.Detach();

        var exception = await _t.Db.Events.SingleAsync(e => e.RecurrenceId != null);

        // RFC 5545: istisna, seri köküyle aynı UID'yi taşır ve RECURRENCE-ID ile ayrışır.
        Assert.Equal(rootUid, exception.Uid);
        Assert.Equal(TestDatabase.Parse("2026-03-03 09:00"), exception.RecurrenceId);
        Assert.Null(exception.RecurrenceRule);
    }

    [Fact]
    public async Task Ayni_ornek_ikinci_kez_duzenlenince_yeni_satir_acilmaz()
    {
        var created = await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Toplantı", "FREQ=DAILY"));
        _t.Detach();

        var rid = TestDatabase.Parse("2026-03-03 09:00");
        await _t.Events.UpdateAsync(created.PrimaryEventId, rid, SeriesEditScope.ThisOnly,
            _t.Input("2026-03-03 14:00", "2026-03-03 15:00", "İlk düzenleme"));
        _t.Detach();
        await _t.Events.UpdateAsync(created.PrimaryEventId, rid, SeriesEditScope.ThisOnly,
            _t.Input("2026-03-03 16:00", "2026-03-03 17:00", "İkinci düzenleme"));
        _t.Detach();

        Assert.Equal(1, await _t.Db.Events.CountAsync(e => e.RecurrenceId != null));
        Assert.Equal(
            ["2026-03-02 09:00", "2026-03-03 16:00", "2026-03-04 09:00"],
            await _t.StartsAsync("2026-03-02 00:00", "2026-03-05 00:00"));
    }

    // ==================================================================
    // Bu ve sonrakiler (ThisAndFuture)
    // ==================================================================

    [Fact]
    public async Task Bu_ve_sonrakiler_seriyi_ikiye_boler()
    {
        var created = await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Eski", "FREQ=DAILY"));
        _t.Detach();

        await _t.Events.UpdateAsync(
            created.PrimaryEventId,
            TestDatabase.Parse("2026-03-04 09:00"),
            SeriesEditScope.ThisAndFuture,
            _t.Input("2026-03-04 11:00", "2026-03-04 12:00", "Yeni", "FREQ=DAILY"));
        _t.Detach();

        // 2-3 mart eski seri (09:00), 4 marttan sonrası yeni seri (11:00).
        Assert.Equal(
            ["2026-03-02 09:00", "2026-03-03 09:00", "2026-03-04 11:00", "2026-03-05 11:00"],
            await _t.StartsAsync("2026-03-02 00:00", "2026-03-06 00:00"));

        Assert.Equal(["Eski", "Eski", "Yeni", "Yeni"],
            await _t.TitlesAsync("2026-03-02 00:00", "2026-03-06 00:00"));
    }

    [Fact]
    public async Task Bolunen_seri_yeni_uid_alir()
    {
        var created = await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Toplantı", "FREQ=DAILY"));
        var originalUid = (await _t.Db.Events.SingleAsync()).Uid;
        _t.Detach();

        await _t.Events.UpdateAsync(created.PrimaryEventId, TestDatabase.Parse("2026-03-04 09:00"),
            SeriesEditScope.ThisAndFuture, _t.Input("2026-03-04 11:00", "2026-03-04 12:00", "Yeni", "FREQ=DAILY"));
        _t.Detach();

        var roots = await _t.Db.Events.Where(e => e.SeriesId == null).ToListAsync();

        Assert.Equal(2, roots.Count);
        // Ayrı seriler ayrı UID taşır; aksi hâlde CalDAV iki kaydı tek etkinlik sanar.
        Assert.Single(roots, r => r.Uid == originalUid);
        Assert.Equal(2, roots.Select(r => r.Uid).Distinct().Count());
    }

    [Fact]
    public async Task Ilk_ornekten_bolme_tum_seriyi_gunceller()
    {
        var created = await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Eski", "FREQ=DAILY"));
        _t.Detach();

        await _t.Events.UpdateAsync(created.PrimaryEventId, TestDatabase.Parse("2026-03-02 09:00"),
            SeriesEditScope.ThisAndFuture, _t.Input("2026-03-02 11:00", "2026-03-02 12:00", "Yeni", "FREQ=DAILY"));
        _t.Detach();

        // Bölmeye gerek yoktur: tek seri kalır.
        Assert.Equal(1, await _t.Db.Events.CountAsync(e => e.SeriesId == null));
        Assert.Equal(["Yeni", "Yeni"], await _t.TitlesAsync("2026-03-02 00:00", "2026-03-04 00:00"));
    }

    [Fact]
    public async Task Count_iceren_seri_bolununce_kalan_sayi_dogru_dagilir()
    {
        // Beş örnek: 2,3,4,5,6 mart. 4 marttan bölünür: eskide 2, yenide 3 kalmalı.
        var created = await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Toplantı", "FREQ=DAILY;COUNT=5"));
        _t.Detach();

        await _t.Events.UpdateAsync(created.PrimaryEventId, TestDatabase.Parse("2026-03-04 09:00"),
            SeriesEditScope.ThisAndFuture, _t.Input("2026-03-04 09:00", "2026-03-04 10:00", "Toplantı", "FREQ=DAILY;COUNT=5"));
        _t.Detach();

        var starts = await _t.StartsAsync("2026-03-01 00:00", "2026-04-01 00:00");

        Assert.Equal(
            ["2026-03-02 09:00", "2026-03-03 09:00", "2026-03-04 09:00", "2026-03-05 09:00", "2026-03-06 09:00"],
            starts);
    }

    [Fact]
    public async Task Bolunme_sonrasi_sonraki_istisnalar_yeni_seriye_baglanir()
    {
        var created = await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Toplantı", "FREQ=DAILY"));
        _t.Detach();

        // 6 marttaki örneği önce ayrıca düzenle.
        await _t.Events.UpdateAsync(created.PrimaryEventId, TestDatabase.Parse("2026-03-06 09:00"),
            SeriesEditScope.ThisOnly, _t.Input("2026-03-06 18:00", "2026-03-06 19:00", "Özel örnek"));
        _t.Detach();

        // Sonra 4 marttan itibaren seriyi böl.
        await _t.Events.UpdateAsync(created.PrimaryEventId, TestDatabase.Parse("2026-03-04 09:00"),
            SeriesEditScope.ThisAndFuture, _t.Input("2026-03-04 11:00", "2026-03-04 12:00", "Yeni", "FREQ=DAILY"));
        _t.Detach();

        var exception = await _t.Db.Events.SingleAsync(e => e.RecurrenceId != null);
        var newRoot = await _t.Db.Events.SingleAsync(e => e.SeriesId == null && e.Title == "Yeni");

        Assert.Equal(newRoot.Id, exception.SeriesId);
        Assert.Equal(newRoot.Uid, exception.Uid);

        // Özel örnek hâlâ kendi saatinde görünür.
        Assert.Contains("2026-03-06 18:00", await _t.StartsAsync("2026-03-06 00:00", "2026-03-07 00:00"));
    }

    // ==================================================================
    // Tüm seri (AllInSeries)
    // ==================================================================

    [Fact]
    public async Task Tum_seri_duzenlemesi_her_ornegi_degistirir()
    {
        var created = await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Eski", "FREQ=DAILY"));
        _t.Detach();

        await _t.Events.UpdateAsync(created.PrimaryEventId, TestDatabase.Parse("2026-03-03 09:00"),
            SeriesEditScope.AllInSeries, _t.Input("2026-03-02 15:00", "2026-03-02 16:00", "Yeni", "FREQ=DAILY"));
        _t.Detach();

        Assert.Equal(
            ["2026-03-02 15:00", "2026-03-03 15:00", "2026-03-04 15:00"],
            await _t.StartsAsync("2026-03-02 00:00", "2026-03-05 00:00"));
    }

    [Fact]
    public async Task Tum_seri_duzenlemesi_sapmis_ornekleri_sifirlar()
    {
        var created = await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Toplantı", "FREQ=DAILY"));
        _t.Detach();

        await _t.Events.UpdateAsync(created.PrimaryEventId, TestDatabase.Parse("2026-03-03 09:00"),
            SeriesEditScope.ThisOnly, _t.Input("2026-03-03 14:00", "2026-03-03 15:00", "Taşınmış"));
        _t.Detach();

        await _t.Events.UpdateAsync(created.PrimaryEventId, TestDatabase.Parse("2026-03-02 09:00"),
            SeriesEditScope.AllInSeries, _t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Güncel", "FREQ=DAILY"));
        _t.Detach();

        // Sapmış örnek silinir; 3 mart yeniden seriden üretilir.
        Assert.Empty(await _t.Db.Events.Where(e => e.RecurrenceId != null).ToListAsync());
        Assert.Equal(
            ["2026-03-02 09:00", "2026-03-03 09:00", "2026-03-04 09:00"],
            await _t.StartsAsync("2026-03-02 00:00", "2026-03-05 00:00"));
    }

    [Fact]
    public async Task Tum_seri_duzenlemesi_silinmis_ornekleri_silinmis_birakir()
    {
        var created = await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Toplantı", "FREQ=DAILY"));
        _t.Detach();

        await _t.Events.DeleteAsync(created.PrimaryEventId, TestDatabase.Parse("2026-03-03 09:00"),
            SeriesEditScope.ThisOnly, _t.UserId);
        _t.Detach();

        await _t.Events.UpdateAsync(created.PrimaryEventId, TestDatabase.Parse("2026-03-02 09:00"),
            SeriesEditScope.AllInSeries, _t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Güncel", "FREQ=DAILY"));
        _t.Detach();

        // Silinen 3 mart geri gelmemeli.
        Assert.Equal(
            ["2026-03-02 09:00", "2026-03-04 09:00"],
            await _t.StartsAsync("2026-03-02 00:00", "2026-03-05 00:00"));
    }
}
