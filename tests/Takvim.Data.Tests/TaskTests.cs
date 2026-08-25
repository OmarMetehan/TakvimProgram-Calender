using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// Görevler. Etkinliklerden ayrı bir dünya oldukları için sınamalar da ayrı:
/// burada süre, çakışma ve zaman dilimi yok; bitiş <b>günü</b>, öncelik ve
/// tamamlanınca ileri taşınan tekrar var.
/// </summary>
public class TaskTests : IDisposable
{
    private readonly TestDatabase _t = new();
    private readonly TaskService _tasks;

    private Guid _listId;

    public TaskTests()
    {
        _tasks = new TaskService(_t.Db, _t.Clock, _t.Zones);

        _listId = _tasks.GetOrCreateDefaultListAsync(_t.UserId).GetAwaiter().GetResult().Id;
        _t.Detach();
    }

    public void Dispose()
    {
        _t.Dispose();
        GC.SuppressFinalize(this);
    }

    // Sınama saati 2026-01-01 09:00 UTC'de duruyor.
    private static readonly LocalDate Today = new(2026, 1, 1);

    private async Task<TaskItem> CreateAsync(
        string title = "Rapor yaz",
        LocalDate? due = null,
        LocalTime? dueTime = null,
        string? rrule = null,
        TaskPriority priority = TaskPriority.Normal,
        Guid? parentId = null)
    {
        var task = await _tasks.CreateAsync(new TaskInput
        {
            TaskListId = _listId,
            Title = title,
            DueDate = due,
            DueTime = dueTime,
            RecurrenceRule = rrule,
            Priority = priority,
            ParentTaskId = parentId,
        });

        _t.Detach();
        return task;
    }

    // ==================================================================
    // Listeler
    // ==================================================================

    [Fact]
    public async Task Varsayilan_liste_kendiliginden_acilir()
    {
        var list = Assert.Single(await _tasks.GetListsAsync(_t.UserId));

        Assert.True(list.IsDefault);
        Assert.Equal("Görevlerim", list.Name);
    }

    [Fact]
    public async Task Varsayilan_liste_iki_kez_acilmaz()
    {
        await _tasks.GetOrCreateDefaultListAsync(_t.UserId);
        _t.Detach();

        Assert.Single(await _tasks.GetListsAsync(_t.UserId));
    }

    [Fact]
    public async Task Varsayilan_liste_silinemez()
    {
        var problem = await _tasks.DeleteListAsync(_listId);

        Assert.NotNull(problem);
        Assert.Single(await _tasks.GetListsAsync(_t.UserId));
    }

    [Fact]
    public async Task Liste_silinince_gorevleri_de_gider()
    {
        var list = await _tasks.CreateListAsync(_t.UserId, "Ev işleri");
        _t.Detach();

        await _tasks.CreateAsync(new TaskInput { TaskListId = list.Id, Title = "Çamaşır" });
        _t.Detach();

        Assert.Null(await _tasks.DeleteListAsync(list.Id));
        _t.Detach();

        Assert.Empty(await _t.Db.Tasks.ToListAsync());
    }

    [Fact]
    public async Task Varsayilan_liste_ustte_kalir()
    {
        await _tasks.CreateListAsync(_t.UserId, "Aaa ile başlayan");
        _t.Detach();

        Assert.True((await _tasks.GetListsAsync(_t.UserId))[0].IsDefault);
    }

    [Fact]
    public async Task Kullanicilarin_listeleri_ayridir()
    {
        var (otherUserId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");

        await _tasks.GetOrCreateDefaultListAsync(otherUserId);
        _t.Detach();

        Assert.Single(await _tasks.GetListsAsync(_t.UserId));
        Assert.Single(await _tasks.GetListsAsync(otherUserId));
    }

    // ==================================================================
    // Oluşturma ve güncelleme
    // ==================================================================

    [Fact]
    public async Task Gorev_olusturulur()
    {
        var task = await CreateAsync("Rapor yaz", Today.PlusDays(2));

        Assert.Equal("Rapor yaz", task.Title);
        Assert.Equal(Today.PlusDays(2), task.DueDate);
        Assert.Equal(TaskState.Todo, task.State);
    }

    [Fact]
    public async Task Bos_baslik_reddedilir()
        => await Assert.ThrowsAsync<ArgumentException>(() => CreateAsync("   "));

    [Fact]
    public async Task Tarihsiz_gorevin_saati_de_olmaz()
    {
        var task = await CreateAsync("Bir ara", due: null, dueTime: new LocalTime(17, 0));

        Assert.Null(task.DueTime);
    }

    [Fact]
    public async Task Tarihsiz_gorev_tekrarlayamaz()
    {
        // Neyin tekrarlayacağı belirsiz olurdu.
        var task = await CreateAsync("Bir ara", due: null, rrule: "FREQ=WEEKLY");

        Assert.Null(task.RecurrenceRule);
    }

    [Fact]
    public async Task Saatsiz_gorevin_bitisi_gun_sonudur()
    {
        var task = await CreateAsync("Rapor", Today);

        Assert.Equal(Today.At(new LocalTime(23, 59)), task.Due);
    }

    [Fact]
    public async Task Gorev_guncellenir()
    {
        var task = await CreateAsync("Eski");

        var updated = await _tasks.UpdateAsync(task.Id, new TaskInput
        {
            TaskListId = _listId,
            Title = "Yeni",
            DueDate = Today.PlusDays(1),
            Priority = TaskPriority.High,
        });

        Assert.Equal("Yeni", updated!.Title);
        Assert.Equal(TaskPriority.High, updated.Priority);
    }

    [Fact]
    public async Task Olmayan_gorev_guncellenemez()
        => Assert.Null(await _tasks.UpdateAsync(
            Guid.NewGuid(), new TaskInput { TaskListId = _listId, Title = "x" }));

    [Fact]
    public async Task Arama_metni_baslik_ve_nottan_kurulur()
    {
        await _tasks.CreateAsync(new TaskInput
        {
            TaskListId = _listId,
            Title = "Bütçe",
            Notes = "Muhasebeye sor",
        });

        _t.Detach();

        var found = await _tasks.GetAsync(_t.UserId, new TaskFilter { SearchTerm = "muhasebe" });

        Assert.Single(found);
    }

    // ==================================================================
    // Tamamlama
    // ==================================================================

    [Fact]
    public async Task Gorev_tamamlanir()
    {
        var task = await CreateAsync();

        var done = await _tasks.SetDoneAsync(task.Id, true, _t.UserId);

        Assert.Equal(TaskState.Done, done!.State);
        Assert.NotNull(done.CompletedAt);
    }

    [Fact]
    public async Task Tamamlama_geri_alinir()
    {
        var task = await CreateAsync();

        await _tasks.SetDoneAsync(task.Id, true, _t.UserId);
        _t.Detach();

        var reopened = await _tasks.SetDoneAsync(task.Id, false, _t.UserId);

        Assert.Equal(TaskState.Todo, reopened!.State);
        Assert.Null(reopened.CompletedAt);
    }

    [Fact]
    public async Task Tamamlanan_gorev_varsayilan_listede_gorunmez()
    {
        var task = await CreateAsync();

        await _tasks.SetDoneAsync(task.Id, true, _t.UserId);
        _t.Detach();

        Assert.Empty(await _tasks.GetAsync(_t.UserId));
        Assert.Single(await _tasks.GetAsync(_t.UserId, new TaskFilter { IncludeDone = true }));
    }

    // ==================================================================
    // Tekrar
    // ==================================================================

    [Fact]
    public async Task Tekrarlayan_gorev_kapanmaz_ileri_tasinir()
    {
        // 1 Ocak 2026 perşembe; haftalık tekrar 8 Ocak'a taşımalı.
        var task = await CreateAsync("Haftalık rapor", Today, rrule: "FREQ=WEEKLY");

        var rolled = await _tasks.SetDoneAsync(task.Id, true, _t.UserId);

        Assert.Equal(TaskState.Todo, rolled!.State);
        Assert.Equal(Today.PlusDays(7), rolled.DueDate);
        Assert.Null(rolled.CompletedAt);
    }

    [Fact]
    public async Task Gec_tamamlanan_tekrar_gecmise_dusmez()
    {
        // Görev üç hafta önceye tarihli; bugünden sonraki ilk tekrara gitmeli,
        // yoksa yeniden gecikmiş olarak doğardı.
        var task = await CreateAsync("Haftalık rapor", Today.PlusDays(-21), rrule: "FREQ=WEEKLY");

        var rolled = await _tasks.SetDoneAsync(task.Id, true, _t.UserId);

        Assert.NotNull(rolled!.DueDate);
        Assert.True(rolled.DueDate > Today, $"yeni tarih geçmişte: {rolled.DueDate}");
    }

    [Fact]
    public async Task Biten_seri_normal_bicimde_kapanir()
    {
        // İki tekrarlı seri: ikincisi tamamlanınca taşınacak yer kalmaz.
        var task = await CreateAsync("İki kez", Today, rrule: "FREQ=WEEKLY;COUNT=2");

        await _tasks.SetDoneAsync(task.Id, true, _t.UserId);
        _t.Detach();

        var closed = await _tasks.SetDoneAsync(task.Id, true, _t.UserId);

        Assert.Equal(TaskState.Done, closed!.State);
    }

    [Fact]
    public async Task Bozuk_tekrar_kurali_cokertmez()
    {
        var task = await CreateAsync("Bozuk", Today, rrule: "BU BIR KURAL DEGIL");

        var done = await _tasks.SetDoneAsync(task.Id, true, _t.UserId);

        Assert.Equal(TaskState.Done, done!.State);
    }

    [Fact]
    public async Task Tekrar_donunce_alt_gorevler_sifirlanir()
    {
        var parent = await CreateAsync("Haftalık rapor", Today, rrule: "FREQ=WEEKLY");
        var child = await CreateAsync("Veriyi topla", parentId: parent.Id);

        await _tasks.SetDoneAsync(child.Id, true, _t.UserId);
        _t.Detach();

        await _tasks.SetDoneAsync(parent.Id, true, _t.UserId);
        _t.Detach();

        // Bu turun işaretleri bir sonraki turda anlamsızdır.
        var reloaded = await _t.Db.Tasks.AsNoTracking().FirstAsync(t => t.Id == child.Id);

        Assert.Equal(TaskState.Todo, reloaded.State);
    }

    // ==================================================================
    // Alt görevler
    // ==================================================================

    [Fact]
    public async Task Alt_gorev_ust_gorevle_gelir()
    {
        var parent = await CreateAsync("Rapor");
        await CreateAsync("Veriyi topla", parentId: parent.Id);

        var top = Assert.Single(await _tasks.GetAsync(_t.UserId));

        Assert.Equal("Rapor", top.Title);
        Assert.Single(top.Subtasks);
    }

    [Fact]
    public async Task Alt_gorev_ust_duzeyde_listelenmez()
    {
        var parent = await CreateAsync("Rapor");
        await CreateAsync("Veriyi topla", parentId: parent.Id);

        Assert.Single(await _tasks.GetAsync(_t.UserId));
    }

    [Fact]
    public async Task Ust_gorev_silinince_alt_gorev_de_silinir()
    {
        var parent = await CreateAsync("Rapor");
        var child = await CreateAsync("Veriyi topla", parentId: parent.Id);

        await _tasks.DeleteAsync(parent.Id);
        _t.Detach();

        var reloaded = await _t.Db.Tasks.AsNoTracking().FirstAsync(t => t.Id == child.Id);

        Assert.NotNull(reloaded.DeletedAt);
    }

    // ==================================================================
    // Sıralama ve süzme
    // ==================================================================

    [Fact]
    public async Task Gecikmisler_once_tarihsizler_sonda()
    {
        await CreateAsync("Tarihsiz");
        await CreateAsync("Gelecek", Today.PlusDays(5));
        await CreateAsync("Gecikmiş", Today.PlusDays(-3));

        var order = (await _tasks.GetAsync(_t.UserId)).Select(t => t.Title).ToList();

        Assert.Equal(["Gecikmiş", "Gelecek", "Tarihsiz"], order);
    }

    [Fact]
    public async Task Ayni_gunde_oncelik_belirler()
    {
        await CreateAsync("Düşük", Today, priority: TaskPriority.Low);
        await CreateAsync("Yüksek", Today, priority: TaskPriority.High);

        var order = (await _tasks.GetAsync(_t.UserId)).Select(t => t.Title).ToList();

        Assert.Equal(["Yüksek", "Düşük"], order);
    }

    [Fact]
    public async Task Ayni_gunde_saatli_gorev_saatsizden_once()
    {
        await CreateAsync("Gün içinde", Today);
        await CreateAsync("Sabah", Today, dueTime: new LocalTime(9, 0));

        var order = (await _tasks.GetAsync(_t.UserId)).Select(t => t.Title).ToList();

        Assert.Equal(["Sabah", "Gün içinde"], order);
    }

    [Fact]
    public async Task Tarih_araligi_suzulur()
    {
        await CreateAsync("Bugün", Today);
        await CreateAsync("Yarın", Today.PlusDays(1));
        await CreateAsync("Gelecek hafta", Today.PlusDays(9));

        var found = await _tasks.GetForDateRangeAsync(_t.UserId, Today, Today.PlusDays(2));

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public async Task Belirli_gune_kadar_suzulur()
    {
        await CreateAsync("Yakın", Today.PlusDays(1));
        await CreateAsync("Uzak", Today.PlusDays(30));
        await CreateAsync("Tarihsiz");

        var found = await _tasks.GetAsync(_t.UserId, new TaskFilter { DueThrough = Today.PlusDays(7) });

        Assert.Equal("Yakın", Assert.Single(found).Title);
    }

    [Fact]
    public async Task Tarihsizler_ayri_suzulur()
    {
        await CreateAsync("Tarihli", Today);
        await CreateAsync("Tarihsiz");

        var found = await _tasks.GetAsync(_t.UserId, new TaskFilter { Undated = true });

        Assert.Equal("Tarihsiz", Assert.Single(found).Title);
    }

    [Fact]
    public async Task Baska_kullanicinin_gorevleri_gelmez()
    {
        await CreateAsync("Benim");

        var (otherUserId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");
        var otherList = await _tasks.GetOrCreateDefaultListAsync(otherUserId);
        _t.Detach();

        await _tasks.CreateAsync(new TaskInput { TaskListId = otherList.Id, Title = "Ayşe'nin" });
        _t.Detach();

        Assert.Equal("Benim", Assert.Single(await _tasks.GetAsync(_t.UserId)).Title);
        Assert.Equal("Ayşe'nin", Assert.Single(await _tasks.GetAsync(otherUserId)).Title);
    }

    [Fact]
    public async Task Gorev_listede_yeniden_siralanir()
    {
        var first = await CreateAsync("Bir");
        await CreateAsync("İki");
        await CreateAsync("Üç");

        await _tasks.ReorderAsync(first.Id, _listId, 2);
        _t.Detach();

        var order = (await _tasks.GetAsync(_t.UserId)).Select(t => t.Title).ToList();

        Assert.Equal(["İki", "Üç", "Bir"], order);
    }

    // ==================================================================
    // Çöp kutusu
    // ==================================================================

    [Fact]
    public async Task Silinen_gorev_listede_gorunmez()
    {
        var task = await CreateAsync();

        await _tasks.DeleteAsync(task.Id);
        _t.Detach();

        Assert.Empty(await _tasks.GetAsync(_t.UserId, new TaskFilter { IncludeDone = true }));
    }

    [Fact]
    public async Task Silinen_gorev_geri_alinir()
    {
        var task = await CreateAsync();

        await _tasks.DeleteAsync(task.Id);
        _t.Detach();
        await _tasks.RestoreAsync(task.Id);
        _t.Detach();

        Assert.Single(await _tasks.GetAsync(_t.UserId));
    }

    [Fact]
    public async Task Suresi_dolan_gorev_kalici_silinir()
    {
        var task = await CreateAsync();

        await _tasks.DeleteAsync(task.Id);
        _t.Detach();

        _t.Clock.Advance(Duration.FromDays(31));

        Assert.Equal(1, await _tasks.PurgeTrashAsync());
        _t.Detach();

        Assert.Empty(await _t.Db.Tasks.ToListAsync());
    }

    [Fact]
    public async Task Suresi_dolmayan_gorev_kalir()
    {
        var task = await CreateAsync();

        await _tasks.DeleteAsync(task.Id);
        _t.Detach();

        _t.Clock.Advance(Duration.FromDays(5));

        Assert.Equal(0, await _tasks.PurgeTrashAsync());
    }

    // ==================================================================
    // Takvime yer ayırma
    // ==================================================================

    [Fact]
    public async Task Goreve_takvimde_yer_ayrilir()
    {
        var task = await CreateAsync("Rapor yaz", Today);

        var eventId = await _tasks.ScheduleAsync(
            task.Id, _t.CalendarId, Today.At(new LocalTime(14, 0)), 90,
            "Europe/Istanbul", _t.UserId, _t.Events);

        _t.Detach();

        Assert.NotNull(eventId);

        var created = await _t.Db.Events.AsNoTracking().FirstAsync(e => e.Id == eventId);

        Assert.Equal("Rapor yaz", created.Title);
        Assert.Equal(Today.At(new LocalTime(15, 30)), created.EndLocal);
    }

    [Fact]
    public async Task Ikinci_zamanlama_yeni_etkinlik_acmaz()
    {
        var task = await CreateAsync("Rapor yaz", Today);

        var first = await _tasks.ScheduleAsync(
            task.Id, _t.CalendarId, Today.At(new LocalTime(14, 0)), 60,
            "Europe/Istanbul", _t.UserId, _t.Events);
        _t.Detach();

        var second = await _tasks.ScheduleAsync(
            task.Id, _t.CalendarId, Today.At(new LocalTime(16, 0)), 60,
            "Europe/Istanbul", _t.UserId, _t.Events);
        _t.Detach();

        Assert.Equal(first, second);
        Assert.Single(await _t.Db.Events.ToListAsync());

        var moved = await _t.Db.Events.AsNoTracking().FirstAsync();
        Assert.Equal(Today.At(new LocalTime(16, 0)), moved.StartLocal);
    }

    [Fact]
    public async Task Zamanlama_kaldirilinca_etkinlik_de_gider()
    {
        var task = await CreateAsync("Rapor yaz", Today);

        await _tasks.ScheduleAsync(
            task.Id, _t.CalendarId, Today.At(new LocalTime(14, 0)), 60,
            "Europe/Istanbul", _t.UserId, _t.Events);
        _t.Detach();

        await _tasks.UnscheduleAsync(task.Id, _t.UserId, _t.Events);
        _t.Detach();

        var reloaded = await _t.Db.Tasks.AsNoTracking().FirstAsync(t => t.Id == task.Id);
        Assert.Null(reloaded.ScheduledEventId);

        // Etkinlik çöp kutusuna gider; görev listede kalır.
        var scheduled = await _t.Db.Events.AsNoTracking().FirstAsync();
        Assert.NotNull(scheduled.DeletedAt);
    }

    [Fact]
    public async Task Etkinlik_silinirse_gorev_kalir()
    {
        var task = await CreateAsync("Rapor yaz", Today);

        var eventId = await _tasks.ScheduleAsync(
            task.Id, _t.CalendarId, Today.At(new LocalTime(14, 0)), 60,
            "Europe/Istanbul", _t.UserId, _t.Events);
        _t.Detach();

        // Kalıcı silme: bağ kopmalı ama görev durmalı.
        await _t.Db.Events.Where(e => e.Id == eventId).ExecuteDeleteAsync();
        _t.Detach();

        var reloaded = await _t.Db.Tasks.AsNoTracking().FirstAsync(t => t.Id == task.Id);

        Assert.Null(reloaded.ScheduledEventId);
        Assert.Equal("Rapor yaz", reloaded.Title);
    }

    [Fact]
    public async Task Olmayan_gorev_zamanlanamaz()
        => Assert.Null(await _tasks.ScheduleAsync(
            Guid.NewGuid(), _t.CalendarId, Today.AtMidnight(), 60,
            "Europe/Istanbul", _t.UserId, _t.Events));
}
