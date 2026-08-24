using Microsoft.EntityFrameworkCore;
using Takvim.Core.Domain;
using Takvim.Core.Permissions;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// Paylaşım değişikliklerinin denetim kaydına düşmesi.
/// <para>
/// "Paylaşım ve erişim denetim kaydı" ancak tek bir yazma yolu varsa
/// güvenilirdir. Bu testler o yolu korur: arayüz paylaşım satırını doğrudan
/// yazarsa günlük eksik kalır ve buradaki sayımlar tutmaz.
/// </para>
/// </summary>
public class SharingAuditTests : IDisposable
{
    private readonly TestDatabase _t = new();
    private readonly SharingService _sharing;
    private readonly Guid _ayse;

    public SharingAuditTests()
    {
        _sharing = new SharingService(_t.Db);
        (_ayse, _) = _t.AddUser("Ayşe Yılmaz", "ayse@ornek.local");
    }

    public void Dispose()
    {
        _t.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<List<ChangeLogEntry>> ShareLogAsync()
        => _t.Db.ChangeLog
            .AsNoTracking()
            .Where(c => c.EntityType == nameof(CalendarShare))
            .OrderBy(c => c.SyncToken)
            .ToListAsync();

    // ==================================================================
    // Paylaşma
    // ==================================================================

    [Fact]
    public async Task Paylasim_kaydi_acilir()
    {
        await _sharing.ShareAsync(_t.CalendarId, _ayse, SharingLevel.FullDetails, _t.UserId);
        _t.Detach();

        var shares = await _sharing.GetSharesAsync(_t.CalendarId);

        Assert.Single(shares);
        Assert.Equal(SharingLevel.FullDetails, shares[0].Level);
    }

    [Fact]
    public async Task Paylasma_denetim_kaydina_yazilir()
    {
        await _sharing.ShareAsync(_t.CalendarId, _ayse, SharingLevel.FreeBusy, _t.UserId);
        _t.Detach();

        var log = await ShareLogAsync();

        Assert.Single(log);
        Assert.Equal(ChangeOperation.Create, log[0].Operation);
        Assert.Equal(_t.UserId, log[0].ActorUserId);
        Assert.Equal(_t.CalendarId, log[0].CalendarId);
        Assert.Contains("Ayşe Yılmaz", log[0].Summary, StringComparison.Ordinal);
        Assert.Contains("meşgul/müsait", log[0].Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ayni_kisiyle_yeniden_paylasmak_seviyeyi_gunceller()
    {
        await _sharing.ShareAsync(_t.CalendarId, _ayse, SharingLevel.FreeBusy, _t.UserId);
        _t.Detach();
        await _sharing.ShareAsync(_t.CalendarId, _ayse, SharingLevel.CanEdit, _t.UserId);
        _t.Detach();

        var shares = await _sharing.GetSharesAsync(_t.CalendarId);

        Assert.Single(shares);
        Assert.Equal(SharingLevel.CanEdit, shares[0].Level);
    }

    [Fact]
    public async Task Takvim_sahibiyle_paylasilamaz()
        => await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sharing.ShareAsync(_t.CalendarId, _t.UserId, SharingLevel.FreeBusy, _t.UserId));

    [Fact]
    public async Task Olmayan_kullaniciyla_paylasilamaz()
        => await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sharing.ShareAsync(_t.CalendarId, Guid.NewGuid(), SharingLevel.FreeBusy, _t.UserId));

    // ==================================================================
    // Güncelleme
    // ==================================================================

    [Fact]
    public async Task Seviye_degisikligi_kaydedilir()
    {
        var share = await _sharing.ShareAsync(_t.CalendarId, _ayse, SharingLevel.FreeBusy, _t.UserId);
        _t.Detach();

        await _sharing.UpdateAsync(share.Id, _t.UserId, s => s.Level = SharingLevel.FullDetails);
        _t.Detach();

        var log = await ShareLogAsync();

        Assert.Equal(2, log.Count);
        Assert.Equal(ChangeOperation.Update, log[1].Operation);
        Assert.NotNull(log[1].BeforeJson);
        Assert.NotNull(log[1].AfterJson);
        Assert.Contains("tüm detaylar", log[1].Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Vekillik_kapatilinca_ozel_oge_yetkisi_de_kapanir()
    {
        var share = await _sharing.ShareAsync(_t.CalendarId, _ayse, SharingLevel.CanEdit, _t.UserId);
        _t.Detach();

        await _sharing.UpdateAsync(share.Id, _t.UserId, s =>
        {
            s.IsDelegate = true;
            s.CanSeePrivateItems = true;
        });
        _t.Detach();

        await _sharing.UpdateAsync(share.Id, _t.UserId, s => s.IsDelegate = false);
        _t.Detach();

        var updated = (await _sharing.GetSharesAsync(_t.CalendarId))[0];

        Assert.False(updated.IsDelegate);
        // Vekil değilken "özel öğeleri görebilir" anlamsızdır; birlikte kapanır.
        Assert.False(updated.CanSeePrivateItems);
    }

    // ==================================================================
    // Kaldırma
    // ==================================================================

    [Fact]
    public async Task Paylasim_kaldirilinca_kayit_silinir_ama_gunluk_kalir()
    {
        var share = await _sharing.ShareAsync(_t.CalendarId, _ayse, SharingLevel.FreeBusy, _t.UserId);
        _t.Detach();

        await _sharing.RevokeAsync(share.Id, _t.UserId);
        _t.Detach();

        Assert.Empty(await _sharing.GetSharesAsync(_t.CalendarId));

        var log = await ShareLogAsync();

        Assert.Equal(2, log.Count);
        Assert.Equal(ChangeOperation.Delete, log[1].Operation);
        // Silinen paylaşımın önceki hâli günlükte durur.
        Assert.NotNull(log[1].BeforeJson);
    }

    [Fact]
    public async Task Olmayan_paylasimi_kaldirmak_hata_vermez()
    {
        await _sharing.RevokeAsync(Guid.NewGuid(), _t.UserId);

        Assert.Empty(await ShareLogAsync());
    }

    // ==================================================================
    // Geçmiş görünümü
    // ==================================================================

    [Fact]
    public async Task Gecmis_en_yeni_once_siralanir()
    {
        var share = await _sharing.ShareAsync(_t.CalendarId, _ayse, SharingLevel.FreeBusy, _t.UserId);
        _t.Detach();
        await _sharing.UpdateAsync(share.Id, _t.UserId, s => s.Level = SharingLevel.CanEdit);
        _t.Detach();

        var history = await _sharing.GetHistoryAsync(_t.CalendarId);

        Assert.Equal(2, history.Count);
        Assert.Equal(ChangeOperation.Update, history[0].Operation);
        Assert.Equal(ChangeOperation.Create, history[1].Operation);
    }

    [Fact]
    public async Task Gecmis_yalnizca_o_takvimi_kapsar()
    {
        var ikinci = new Calendar { Name = "İş", OwnerUserId = _t.UserId };
        _t.Db.Calendars.Add(ikinci);
        _t.Db.SaveChanges();
        _t.Detach();

        await _sharing.ShareAsync(_t.CalendarId, _ayse, SharingLevel.FreeBusy, _t.UserId);
        _t.Detach();
        await _sharing.ShareAsync(ikinci.Id, _ayse, SharingLevel.FullDetails, _t.UserId);
        _t.Detach();

        Assert.Single(await _sharing.GetHistoryAsync(_t.CalendarId));
        Assert.Single(await _sharing.GetHistoryAsync(ikinci.Id));
    }

    // ==================================================================
    // İzin motoruyla uçtan uca
    // ==================================================================

    [Fact]
    public async Task Servisle_yapilan_paylasim_erisim_haritasina_yansir()
    {
        await _sharing.ShareAsync(_t.CalendarId, _ayse, SharingLevel.CanEdit, _t.UserId);
        _t.Detach();

        var scope = await _t.Permissions.LoadAsync(_ayse);

        Assert.Equal(SharingLevel.CanEdit, scope.LevelFor(_t.CalendarId));
        Assert.True(scope.CanWriteTo(_t.CalendarId));
    }

    [Fact]
    public async Task Paylasim_kaldirilinca_erisim_de_kalkar()
    {
        var share = await _sharing.ShareAsync(_t.CalendarId, _ayse, SharingLevel.FullDetails, _t.UserId);
        _t.Detach();
        await _sharing.RevokeAsync(share.Id, _t.UserId);
        _t.Detach();

        var scope = await _t.Permissions.LoadAsync(_ayse);

        Assert.Equal(SharingLevel.None, scope.LevelFor(_t.CalendarId));
    }
}
