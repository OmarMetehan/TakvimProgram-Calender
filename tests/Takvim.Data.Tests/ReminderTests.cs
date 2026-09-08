using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// Hatırlatıcı motoru. Sınanan asıl davranış tekrar çalmama: aynı örnek için
/// iki kez, ya da uygulama her açıldığında geçmiş etkinlikler için çalmamalı.
/// </summary>
public class ReminderTests : IDisposable
{
    private readonly TestDatabase _t = new();

    public void Dispose()
    {
        _t.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Saati İstanbul yerel saatine kurar.</summary>
    private void SetNow(string local) => _t.Clock.Reset(_t.Utc(local));

    // ------------------------------------------------------------------

    [Fact]
    public async Task Zamani_gelmeyen_hatirlatici_calmaz()
    {
        SetNow("2026-03-02 08:00");
        await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", reminders: [new ReminderInput(10)]));
        _t.Detach();

        // 08:00'de, 08:50'de çalacak bir hatırlatıcı henüz zamanı gelmemiştir.
        Assert.Empty(await _t.Reminders.GetDueAsync(_t.UserId));
    }

    [Fact]
    public async Task Zamani_gelen_hatirlatici_calar()
    {
        SetNow("2026-03-02 08:00");
        await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Toplantı", reminders: [new ReminderInput(10)]));
        _t.Detach();

        SetNow("2026-03-02 08:51");
        var due = await _t.Reminders.GetDueAsync(_t.UserId);

        Assert.Single(due);
        Assert.Equal("Toplantı", due[0].Title);
        Assert.Equal(10, due[0].MinutesBefore);
    }

    [Fact]
    public async Task Kapatilan_hatirlatici_bir_daha_calmaz()
    {
        SetNow("2026-03-02 08:00");
        await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", reminders: [new ReminderInput(10)]));
        _t.Detach();

        SetNow("2026-03-02 08:51");
        var due = await _t.Reminders.GetDueAsync(_t.UserId);
        Assert.Single(due);

        await _t.Reminders.MarkFiredAsync(due[0].ReminderId, due[0].OccurrenceStartUtc);
        _t.Detach();

        SetNow("2026-03-02 08:55");
        Assert.Empty(await _t.Reminders.GetDueAsync(_t.UserId));
    }

    [Fact]
    public async Task Cok_gecmiste_kalan_hatirlatici_calmaz()
    {
        // Uygulama günlerce kapalı kaldıysa açılışta eski hatırlatıcılar
        // topluca çalmamalıdır.
        SetNow("2026-03-02 08:00");
        await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", reminders: [new ReminderInput(10)]));
        _t.Detach();

        SetNow("2026-03-05 12:00");
        Assert.Empty(await _t.Reminders.GetDueAsync(_t.UserId));
    }

    [Fact]
    public async Task Yeni_kacirilan_hatirlatici_hosgoru_suresinde_calar()
    {
        SetNow("2026-03-02 08:00");
        await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", reminders: [new ReminderInput(10)]));
        _t.Detach();

        // Etkinlik 10 dakika önce başladı; hoşgörü süresi 30 dakika.
        SetNow("2026-03-02 09:10");
        Assert.Single(await _t.Reminders.GetDueAsync(_t.UserId));
    }

    // ------------------------------------------------------------------
    // Tekrarlayan etkinlikler
    // ------------------------------------------------------------------

    [Fact]
    public async Task Tekrarlayan_etkinlikte_her_ornek_icin_ayri_calar()
    {
        SetNow("2026-03-02 08:00");
        await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Günlük", "FREQ=DAILY",
                reminders: [new ReminderInput(10)]));
        _t.Detach();

        // Birinci gün
        SetNow("2026-03-02 08:51");
        var first = await _t.Reminders.GetDueAsync(_t.UserId);
        Assert.Single(first);
        await _t.Reminders.MarkFiredAsync(first[0].ReminderId, first[0].OccurrenceStartUtc);
        _t.Detach();

        // Aynı gün ikinci kez çalmaz
        SetNow("2026-03-02 08:55");
        Assert.Empty(await _t.Reminders.GetDueAsync(_t.UserId));

        // Ertesi gün yeniden çalar
        SetNow("2026-03-03 08:51");
        var second = await _t.Reminders.GetDueAsync(_t.UserId);
        Assert.Single(second);
        Assert.Equal(_t.Utc("2026-03-03 09:00"), second[0].OccurrenceStartUtc);
    }

    [Fact]
    public async Task Silinen_ornek_icin_hatirlatici_calmaz()
    {
        SetNow("2026-03-02 08:00");
        var created = await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", "Günlük", "FREQ=DAILY",
                reminders: [new ReminderInput(10)]));
        _t.Detach();

        await _t.Events.DeleteAsync(created.PrimaryEventId, TestDatabase.Parse("2026-03-03 09:00"),
            SeriesEditScope.ThisOnly, _t.UserId);
        _t.Detach();

        SetNow("2026-03-03 08:51");
        Assert.Empty(await _t.Reminders.GetDueAsync(_t.UserId));
    }

    // ------------------------------------------------------------------
    // Erteleme
    // ------------------------------------------------------------------

    [Fact]
    public async Task Ertelenen_hatirlatici_sure_dolunca_yeniden_calar()
    {
        SetNow("2026-03-02 08:00");
        await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", reminders: [new ReminderInput(10)]));
        _t.Detach();

        SetNow("2026-03-02 08:51");
        var due = await _t.Reminders.GetDueAsync(_t.UserId);
        await _t.Reminders.SnoozeAsync(due[0].ReminderId, Duration.FromMinutes(5));
        _t.Detach();

        // Erteleme sürerken sessiz
        SetNow("2026-03-02 08:53");
        Assert.Empty(await _t.Reminders.GetDueAsync(_t.UserId));

        // Süre dolunca yeniden çalar
        SetNow("2026-03-02 08:57");
        Assert.Single(await _t.Reminders.GetDueAsync(_t.UserId));
    }

    // ------------------------------------------------------------------
    // Silinen ve iptal edilen etkinlikler
    // ------------------------------------------------------------------

    [Fact]
    public async Task Cop_kutusundaki_etkinligin_hatirlaticisi_calmaz()
    {
        SetNow("2026-03-02 08:00");
        var created = await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00", reminders: [new ReminderInput(10)]));
        _t.Detach();

        await _t.Events.DeleteAsync(created.PrimaryEventId, null, SeriesEditScope.AllInSeries, _t.UserId);
        _t.Detach();

        SetNow("2026-03-02 08:51");
        Assert.Empty(await _t.Reminders.GetDueAsync(_t.UserId));
    }

    [Fact]
    public async Task Birden_cok_hatirlatici_ayri_ayri_calar()
    {
        SetNow("2026-03-02 08:00");
        await _t.Events.CreateAsync(
            _t.Input("2026-03-02 09:00", "2026-03-02 10:00",
                reminders: [new ReminderInput(30), new ReminderInput(5)]));
        _t.Detach();

        // 08:31: yalnızca 30 dakikalık olan çalar.
        SetNow("2026-03-02 08:31");
        var first = await _t.Reminders.GetDueAsync(_t.UserId);
        Assert.Single(first);
        Assert.Equal(30, first[0].MinutesBefore);

        await _t.Reminders.MarkFiredAsync(first[0].ReminderId, first[0].OccurrenceStartUtc);
        _t.Detach();

        // 08:56: beş dakikalık olan çalar.
        SetNow("2026-03-02 08:56");
        var second = await _t.Reminders.GetDueAsync(_t.UserId);
        Assert.Single(second);
        Assert.Equal(5, second[0].MinutesBefore);
    }

    [Fact]
    public async Task Hatirlatici_etkinligin_rengini_tasir()
    {
        SetNow("2026-03-02 08:00");
        var input = _t.Input("2026-03-02 09:00", "2026-03-02 10:00", reminders: [new ReminderInput(10)])
            with { Color = "grape" };

        await _t.Events.CreateAsync(input);
        _t.Detach();

        SetNow("2026-03-02 08:51");
        Assert.Equal("grape", (await _t.Reminders.GetDueAsync(_t.UserId))[0].Color);
    }

    [Fact]
    public async Task Hatirlaticisiz_etkinlik_hicbir_sey_uretmez()
    {
        SetNow("2026-03-02 08:00");
        await _t.Events.CreateAsync(_t.Input("2026-03-02 09:00", "2026-03-02 10:00"));
        _t.Detach();

        SetNow("2026-03-02 08:51");
        Assert.Empty(await _t.Reminders.GetDueAsync(_t.UserId));
        Assert.Empty(await _t.Db.Reminders.ToListAsync());
    }
}
