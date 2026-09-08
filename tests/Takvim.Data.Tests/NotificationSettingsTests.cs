using NodaTime;
using Takvim.Core.Notifications;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// Bildirim tercihleri ve sessiz saatlerin hatırlatıcı motoruna etkisi.
/// <para>
/// Asıl sınanan kural: sessizlik hatırlatıcıyı <b>tüketmez</b>. Sessiz saatlerde
/// gösterilmeyen bir uyarı, pencere kapandığında hâlâ zamanındaysa çalar —
/// kapatılmış sayılıp kaybolmaz.
/// </para>
/// </summary>
public class NotificationSettingsTests : IDisposable
{
    private readonly TestDatabase _t = new();

    public void Dispose()
    {
        _t.Dispose();
        GC.SuppressFinalize(this);
    }

    private void SetNow(string local) => _t.Clock.Reset(_t.Utc(local));

    /// <summary>Sessiz saatleri kurar. Varsayılan pencere gece 22:00–08:00.</summary>
    private async Task Quiet(
        bool enabled = true,
        int startHour = 22,
        int endHour = 8,
        bool onDaysOff = false,
        bool remindersEnabled = true)
    {
        await _t.Notifications.SaveAsync(_t.UserId, new NotificationSettings(
            remindersEnabled,
            new QuietHours(enabled, new LocalTime(startHour, 0), new LocalTime(endHour, 0), onDaysOff)));

        _t.Detach();
    }

    // ==================================================================
    // Tercihlerin kaydı
    // ==================================================================

    [Fact]
    public async Task Varsayilan_olarak_bildirimler_aciktir()
    {
        var settings = await _t.Notifications.GetAsync(_t.UserId);

        Assert.True(settings.RemindersEnabled);
        Assert.False(settings.Quiet.Enabled);
    }

    [Fact]
    public async Task Kaydedilen_tercihler_geri_okunur()
    {
        await Quiet(startHour: 21, endHour: 7, onDaysOff: true);

        var settings = await _t.Notifications.GetAsync(_t.UserId);

        Assert.True(settings.Quiet.Enabled);
        Assert.Equal(new LocalTime(21, 0), settings.Quiet.Start);
        Assert.Equal(new LocalTime(7, 0), settings.Quiet.End);
        Assert.True(settings.Quiet.AllDayOnDaysOff);
    }

    /// <summary>
    /// Sıfır uzunluklu pencere hiçbir şey susturmaz; kullanıcı ise sessiz
    /// saatleri kurduğunu sanır. Kaydettirilmez.
    /// </summary>
    [Fact]
    public async Task Ayni_baslangic_ve_bitis_kaydedilmez()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Quiet(startHour: 9, endHour: 9));

        Assert.False((await _t.Notifications.GetAsync(_t.UserId)).Quiet.Enabled);
    }

    /// <summary>Pencere kapalıyken saatlerin eşitliği bir şey ifade etmez, engellenmez.</summary>
    [Fact]
    public async Task Kapali_pencerede_ayni_saatler_kaydedilebilir()
    {
        await Quiet(enabled: false, startHour: 9, endHour: 9);

        Assert.Equal(new LocalTime(9, 0), (await _t.Notifications.GetAsync(_t.UserId)).Quiet.Start);
    }

    // ==================================================================
    // Bildirimlerin tümden kapatılması
    // ==================================================================

    [Fact]
    public async Task Bildirimler_kapaliyken_hatirlatici_calmaz()
    {
        SetNow("2026-03-02 08:00");
        await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", reminders: [new ReminderInput(10)]));
        _t.Detach();

        await Quiet(enabled: false, remindersEnabled: false);

        SetNow("2026-03-02 08:51");
        Assert.Empty(await _t.Reminders.GetDueAsync(_t.UserId));
    }

    /// <summary>
    /// Kapalıyken atlanan hatırlatıcı tüketilmez: bildirimler yeniden açılınca,
    /// etkinlik hâlâ yaklaşıyorsa uyarı çıkar.
    /// </summary>
    [Fact]
    public async Task Yeniden_acilinca_ayni_hatirlatici_calar()
    {
        SetNow("2026-03-02 08:00");
        await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", reminders: [new ReminderInput(10)]));
        _t.Detach();

        await Quiet(enabled: false, remindersEnabled: false);

        SetNow("2026-03-02 08:51");
        Assert.Empty(await _t.Reminders.GetDueAsync(_t.UserId));

        await Quiet(enabled: false, remindersEnabled: true);

        SetNow("2026-03-02 08:55");
        Assert.Single(await _t.Reminders.GetDueAsync(_t.UserId));
    }

    // ==================================================================
    // Sessiz saatler
    // ==================================================================

    [Fact]
    public async Task Sessiz_saatlerde_hatirlatici_gosterilmez()
    {
        SetNow("2026-03-02 20:00");
        await _t.Events.CreateAsync(
            _t.Input("2026-03-02 23:00", "2026-03-03 00:00", reminders: [new ReminderInput(30)]));
        _t.Detach();

        await Quiet();

        // 22:30'da çalacaktı; 22:00–08:00 penceresinin içinde kalıyor.
        SetNow("2026-03-02 22:31");
        Assert.Empty(await _t.Reminders.GetDueAsync(_t.UserId));
    }

    /// <summary>
    /// Sessizlik biter bitmez bekleyen uyarı çıkar: 08:30'daki toplantının bir
    /// saat önceden kurulmuş hatırlatıcısı 07:30'da susturulur, 08:00'de görünür.
    /// </summary>
    [Fact]
    public async Task Pencere_kapaninca_bekleyen_hatirlatici_calar()
    {
        SetNow("2026-03-02 06:00");
        await _t.Events.CreateAsync(
            _t.Input("2026-03-02 08:30", "2026-03-02 09:30", "Sabah toplantısı",
                     reminders: [new ReminderInput(60)]));
        _t.Detach();

        await Quiet();

        SetNow("2026-03-02 07:31");
        Assert.Empty(await _t.Reminders.GetDueAsync(_t.UserId));

        SetNow("2026-03-02 08:00");
        var due = await _t.Reminders.GetDueAsync(_t.UserId);

        Assert.Single(due);
        Assert.Equal("Sabah toplantısı", due[0].Title);
    }

    [Fact]
    public async Task Pencere_disinda_hatirlatici_normal_calar()
    {
        SetNow("2026-03-02 08:00");
        await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", reminders: [new ReminderInput(10)]));
        _t.Detach();

        await Quiet();

        SetNow("2026-03-02 08:51");
        Assert.Single(await _t.Reminders.GetDueAsync(_t.UserId));
    }

    /// <summary>Cumartesi çalışılmaz; kural açıkken gündüz de sessizdir.</summary>
    [Fact]
    public async Task Calisilmayan_gunde_butun_gun_sessizdir()
    {
        SetNow("2026-03-07 08:00");
        await _t.Events.CreateAsync(
            _t.Input("2026-03-07 12:00", "2026-03-07 13:00", reminders: [new ReminderInput(10)]));
        _t.Detach();

        await Quiet(onDaysOff: true);

        SetNow("2026-03-07 11:51");
        Assert.Empty(await _t.Reminders.GetDueAsync(_t.UserId));
    }

    /// <summary>Aynı hatırlatıcı, kural kapalıyken cumartesi de çalar.</summary>
    [Fact]
    public async Task Kural_kapaliyken_calisilmayan_gunde_calar()
    {
        SetNow("2026-03-07 08:00");
        await _t.Events.CreateAsync(
            _t.Input("2026-03-07 12:00", "2026-03-07 13:00", reminders: [new ReminderInput(10)]));
        _t.Detach();

        await Quiet(onDaysOff: false);

        SetNow("2026-03-07 11:51");
        Assert.Single(await _t.Reminders.GetDueAsync(_t.UserId));
    }

    /// <summary>Sessizliğin nedeni ve bitişi arayüzde gösterilir; doğru olmalı.</summary>
    [Fact]
    public async Task Sessizlik_durumu_bitis_saatini_bildirir()
    {
        await Quiet();
        SetNow("2026-03-02 23:00");

        var state = await _t.Notifications.EvaluateAsync(_t.UserId);

        Assert.True(state.IsSilenced);
        Assert.Equal(new LocalDateTime(2026, 3, 3, 8, 0), state.EndsAt);
    }

    [Fact]
    public async Task Bildirimler_kapaliyken_bitis_saati_yoktur()
    {
        await Quiet(enabled: false, remindersEnabled: false);
        SetNow("2026-03-02 12:00");

        var state = await _t.Notifications.EvaluateAsync(_t.UserId);

        Assert.True(state.IsSilenced);
        Assert.Null(state.EndsAt);
    }

    // ==================================================================
    // Hesap yalıtımı
    // ==================================================================

    /// <summary>
    /// Hatırlatıcı takvim sahibine çalar. Başka bir hesabın etkinliği, o hesaba
    /// geçilmediği sürece bu ekranda uyarı çıkarmaz — başlığı da görünmez.
    /// </summary>
    [Fact]
    public async Task Baska_hesabin_hatirlaticisi_bu_hesaba_calmaz()
    {
        var (otherUserId, otherCalendarId) = _t.AddUser("Ayşe", "ayse@ornek.local");

        SetNow("2026-03-02 08:00");
        await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Ayşe'nin toplantısı",
                     reminders: [new ReminderInput(10)])
            with { CalendarId = otherCalendarId, ActorUserId = otherUserId });
        _t.Detach();

        SetNow("2026-03-02 08:51");

        Assert.Empty(await _t.Reminders.GetDueAsync(_t.UserId));
        Assert.Single(await _t.Reminders.GetDueAsync(otherUserId));
    }

    /// <summary>Sessiz saatler hesaba özeldir: birinin sessizliği ötekini susturmaz.</summary>
    [Fact]
    public async Task Sessiz_saatler_yalnizca_kendi_hesabini_susturur()
    {
        var (otherUserId, otherCalendarId) = _t.AddUser("Ayşe", "ayse@ornek.local");

        SetNow("2026-03-02 20:00");
        await _t.Events.CreateAsync(
            _t.Input("2026-03-02 23:00", "2026-03-03 00:00", reminders: [new ReminderInput(30)])
            with { CalendarId = otherCalendarId, ActorUserId = otherUserId });
        _t.Detach();

        // Sessiz saatler yalnızca varsayılan hesapta açık.
        await Quiet();

        SetNow("2026-03-02 22:31");
        Assert.Single(await _t.Reminders.GetDueAsync(otherUserId));
    }
}
