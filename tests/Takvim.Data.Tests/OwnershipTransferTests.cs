using Microsoft.EntityFrameworkCore;
using Takvim.Core.Domain;
using Takvim.Core.Permissions;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// Takvim sahipliğinin devri. Sahiplik izin tablosunun sıfırıncı satırıdır —
/// sahip her şeyi görür — bu yüzden devir hem yeni sahibin fazlalık paylaşım
/// satırını hem de eski sahibin erişimini doğru bırakmalıdır.
/// </summary>
public class OwnershipTransferTests : IDisposable
{
    private readonly TestDatabase _t = new();
    private readonly SharingService _sharing;

    public OwnershipTransferTests() => _sharing = new SharingService(_t.Db);

    public void Dispose()
    {
        _t.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Devredilebilir bir takvim; kişisel takvim devredilemez.</summary>
    private async Task<Guid> CreateSharedCalendarAsync(string name = "Ekip")
    {
        var calendar = new Calendar
        {
            Name = name,
            OwnerUserId = _t.UserId,
            Kind = CalendarKind.Team,
        };

        _t.Db.Calendars.Add(calendar);
        await _t.Db.SaveChangesAsync();
        _t.Detach();

        return calendar.Id;
    }

    private Task<Calendar> ReadAsync(Guid calendarId)
        => _t.Db.Calendars.AsNoTracking().FirstAsync(c => c.Id == calendarId);

    // ==================================================================

    [Fact]
    public async Task Sahiplik_devredilir()
    {
        var calendarId = await CreateSharedCalendarAsync();
        var (newOwnerId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");

        Assert.Null(await _sharing.TransferOwnershipAsync(calendarId, newOwnerId, _t.UserId));
        _t.Detach();

        Assert.Equal(newOwnerId, (await ReadAsync(calendarId)).OwnerUserId);
    }

    [Fact]
    public async Task Eski_sahip_tam_denetimle_kalir()
    {
        var calendarId = await CreateSharedCalendarAsync();
        var (newOwnerId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");

        await _sharing.TransferOwnershipAsync(calendarId, newOwnerId, _t.UserId);
        _t.Detach();

        var share = Assert.Single(await _sharing.GetSharesAsync(calendarId));

        Assert.Equal(_t.UserId, share.GranteeUserId);
        Assert.Equal(SharingLevel.FullControl, share.Level);
    }

    [Fact]
    public async Task Yeni_sahibin_eski_paylasimi_silinir()
    {
        var calendarId = await CreateSharedCalendarAsync();
        var (newOwnerId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");

        // Devirden önce zaten okuma yetkisi vardı.
        _t.Share(calendarId, newOwnerId, SharingLevel.TitleLocation);

        await _sharing.TransferOwnershipAsync(calendarId, newOwnerId, _t.UserId);
        _t.Detach();

        var shares = await _sharing.GetSharesAsync(calendarId);

        // Sahip zaten her şeyi görür; eski satır anlamını yitirir.
        Assert.DoesNotContain(shares, s => s.GranteeUserId == newOwnerId);
    }

    [Fact]
    public async Task Eski_sahibin_var_olan_paylasimi_yukseltilir()
    {
        var calendarId = await CreateSharedCalendarAsync();
        var (newOwnerId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");

        // Yapay ama mümkün: sahibe kendi takviminde bir paylaşım satırı açılmış.
        _t.Db.CalendarShares.Add(new CalendarShare
        {
            CalendarId = calendarId,
            GranteeUserId = _t.UserId,
            Level = SharingLevel.FreeBusy,
        });

        await _t.Db.SaveChangesAsync();
        _t.Detach();

        await _sharing.TransferOwnershipAsync(calendarId, newOwnerId, _t.UserId);
        _t.Detach();

        var share = Assert.Single(await _sharing.GetSharesAsync(calendarId));

        Assert.Equal(SharingLevel.FullControl, share.Level);
    }

    [Fact]
    public async Task Devirden_sonra_yeni_sahip_takvimi_gorur()
    {
        var calendarId = await CreateSharedCalendarAsync();
        var (newOwnerId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");

        await _sharing.TransferOwnershipAsync(calendarId, newOwnerId, _t.UserId);
        _t.Detach();

        var visible = await _t.Permissions.GetVisibleCalendarsAsync(newOwnerId);

        Assert.Contains(visible, c => c.Id == calendarId);
    }

    [Fact]
    public async Task Devirden_sonra_eski_sahip_de_gorur()
    {
        var calendarId = await CreateSharedCalendarAsync();
        var (newOwnerId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");

        await _sharing.TransferOwnershipAsync(calendarId, newOwnerId, _t.UserId);
        _t.Detach();

        var visible = await _t.Permissions.GetVisibleCalendarsAsync(_t.UserId);

        Assert.Contains(visible, c => c.Id == calendarId);
    }

    // ==================================================================
    // Reddedilen devirler
    // ==================================================================

    [Fact]
    public async Task Kisisel_takvim_devredilemez()
    {
        var (newOwnerId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");

        var problem = await _sharing.TransferOwnershipAsync(_t.CalendarId, newOwnerId, _t.UserId);

        Assert.NotNull(problem);
        Assert.Contains("Kişisel", problem, StringComparison.Ordinal);
        Assert.Equal(_t.UserId, (await ReadAsync(_t.CalendarId)).OwnerUserId);
    }

    [Fact]
    public async Task Sahibi_olmayan_devredemez()
    {
        var calendarId = await CreateSharedCalendarAsync();
        var (otherId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");
        var (thirdId, _) = _t.AddUser("Ali", "ali@ornek.local");

        // Tam denetim bile sahiplik değildir.
        _t.Share(calendarId, otherId, SharingLevel.FullControl);

        var problem = await _sharing.TransferOwnershipAsync(calendarId, thirdId, otherId);

        Assert.NotNull(problem);
        Assert.Equal(_t.UserId, (await ReadAsync(calendarId)).OwnerUserId);
    }

    [Fact]
    public async Task Kendine_devredilemez()
    {
        var calendarId = await CreateSharedCalendarAsync();

        Assert.NotNull(await _sharing.TransferOwnershipAsync(calendarId, _t.UserId, _t.UserId));
    }

    [Fact]
    public async Task Olmayan_hesaba_devredilemez()
    {
        var calendarId = await CreateSharedCalendarAsync();

        var problem = await _sharing.TransferOwnershipAsync(calendarId, Guid.NewGuid(), _t.UserId);

        Assert.NotNull(problem);
        Assert.Equal(_t.UserId, (await ReadAsync(calendarId)).OwnerUserId);
    }

    [Fact]
    public async Task Olmayan_takvim_devredilemez()
        => Assert.NotNull(await _sharing.TransferOwnershipAsync(Guid.NewGuid(), _t.UserId, _t.UserId));

    // ==================================================================

    [Fact]
    public async Task Devir_gecmise_yazilir()
    {
        var calendarId = await CreateSharedCalendarAsync();
        var (newOwnerId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");

        await _sharing.TransferOwnershipAsync(calendarId, newOwnerId, _t.UserId);
        _t.Detach();

        var entries = await _t.Db.ChangeLog
            .AsNoTracking()
            .Where(c => c.CalendarId == calendarId)
            .ToListAsync();

        Assert.Contains(entries, e => e.Summary != null
                                   && e.Summary.Contains("sahipliği", StringComparison.Ordinal));
    }
}
