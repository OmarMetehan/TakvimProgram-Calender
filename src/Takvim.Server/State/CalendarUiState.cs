using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Localization;
using Takvim.Core.Recurrence;
using Takvim.Core.Time;
using Takvim.Data;
using Takvim.Data.Services;

namespace Takvim.Server.State;

/// <summary>Kullanıcıya gösterilen ve geri alınabilen son işlem.</summary>
public sealed record UndoPrompt(Guid OperationId, string Message);

/// <summary>
/// Bir Blazor devresinin arayüz durumu: görünüm ayarları, yüklenmiş veriler ve
/// bildirimler. Bileşenler doğrudan veritabanına gitmez, buradan geçer.
/// <para>
/// Veri erişimi her işlemde yeni bir kapsamda yapılır. Blazor Server'da devre
/// saatlerce açık kalabilir; tek bir <c>DbContext</c>'i devre boyunca canlı
/// tutmak hem bellek biriktirir hem de eşzamanlı sorgularda çakışır.
/// </para>
/// </summary>
public sealed class CalendarUiState(
    IServiceScopeFactory scopeFactory,
    TimeZoneService timeZones,
    TurkishHolidays holidays,
    IClock clock)
{
    private readonly List<Calendar> _calendars = [];
    private readonly List<User> _users = [];
    private readonly List<Category> _categories = [];
    private List<EventOccurrence> _occurrences = [];
    private Dictionary<LocalDate, DaySchedule> _schedules = [];
    private List<TaskItem> _tasks = [];

    /// <summary>Görünüm ya da veri değiştiğinde tetiklenir.</summary>
    public event Action? Changed;

    // ------------------------------------------------------------------
    // Durum
    // ------------------------------------------------------------------

    public CalendarViewState View { get; private set; } = new();

    public IReadOnlyList<Calendar> Calendars => _calendars;
    public IReadOnlyList<Category> Categories => _categories;
    public IReadOnlyList<EventOccurrence> Occurrences => _occurrences;

    /// <summary>Görünen günlerin mesai düzeni; ızgaranın soluk alanları buradan çizilir.</summary>
    public IReadOnlyDictionary<LocalDate, DaySchedule> Schedules => _schedules;

    /// <summary>Görünen aralıkta bitiş tarihi olan görevler.</summary>
    public IReadOnlyList<TaskItem> Tasks => _tasks;

    /// <summary>Bir günün görevleri; ızgaradaki gün başlıkları bunu okur.</summary>
    public IEnumerable<TaskItem> TasksOn(LocalDate date) => _tasks.Where(t => t.DueDate == date);

    public bool IsLoading { get; private set; }
    public UndoPrompt? PendingUndo { get; private set; }
    public string? StatusMessage { get; private set; }

    /// <summary>
    /// Şu anda takvimi görüntülenen kullanıcı. Tek makinede birden çok yerel
    /// hesap olabildiği için tüm okuma ve yazma yolları bu kimliği kullanır.
    /// </summary>
    public Guid ActiveUserId { get; private set; } = CalendarBootstrapper.LocalUserId;

    public User? ActiveUser { get; private set; }

    /// <summary>Bu makinedeki tüm hesaplar; kullanıcı değiştirici bunları listeler.</summary>
    public IReadOnlyList<User> Users => _users;

    /// <summary>Aktif kullanıcının zaman dilimi. Tüm görünümler bu dilimde çizilir.</summary>
    public string ZoneId { get; private set; } = TimeZoneService.DefaultZoneId;

    public LocalDate Today => timeZones.TodayIn(ZoneId, clock);

    public LocalDateTime Now => timeZones.ToLocal(clock.GetCurrentInstant(), ZoneId);

    /// <summary>Kenar çubuğunda işaretli, yani ızgarada çizilen takvimler.</summary>
    public IEnumerable<Calendar> VisibleCalendars
        => _calendars.Where(c => !View.HiddenCalendarIds.Contains(c.Id));

    // ------------------------------------------------------------------
    // Yükleme
    // ------------------------------------------------------------------

    /// <summary>Takvimleri, kategorileri ve hesapları okur. Açılışta bir kez çağrılır.</summary>
    public async Task InitializeAsync(
        CalendarViewState view, Guid? activeUserId = null, CancellationToken ct = default)
    {
        View = view;
        if (activeUserId is { } id) ActiveUserId = id;

        await LoadUserContextAsync(ct).ConfigureAwait(false);
        await ReloadAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Kullanıcıyı değiştirir. Tek makinede birden çok hesap olduğu için,
    /// davetleri ve paylaşımları sınamanın yolu budur.
    /// </summary>
    public async Task SwitchUserAsync(Guid userId, CancellationToken ct = default)
    {
        if (userId == ActiveUserId) return;

        ActiveUserId = userId;

        // Gizlenen takvimler önceki kullanıcıya aitti; seçim sıfırlanır.
        View = View with { HiddenCalendarIds = [], CategoryFilter = [] };

        await LoadUserContextAsync(ct).ConfigureAwait(false);
        await ReloadAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Aktif kullanıcıya göre hesap listesini, takvimleri ve kategorileri tazeler.</summary>
    public async Task LoadUserContextAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TakvimDbContext>();
        var permissions = scope.ServiceProvider.GetRequiredService<CalendarPermissions>();

        _users.Clear();
        _users.AddRange(await db.Users
            .AsNoTracking()
            .OrderBy(u => u.DisplayName)
            .ToListAsync(ct).ConfigureAwait(false));

        // Hesap silinmişse ilk hesaba düşülür; boş bir ekranla kalınmaz.
        ActiveUser = _users.FirstOrDefault(u => u.Id == ActiveUserId) ?? _users.FirstOrDefault();
        if (ActiveUser is not null) ActiveUserId = ActiveUser.Id;

        if (ActiveUser is not null && timeZones.IsKnown(ActiveUser.TimeZoneId))
            ZoneId = ActiveUser.TimeZoneId;

        _calendars.Clear();
        _calendars.AddRange(await permissions
            .GetVisibleCalendarsAsync(ActiveUserId, ct).ConfigureAwait(false));

        _categories.Clear();
        _categories.AddRange(await db.Categories
            .AsNoTracking()
            .Where(c => c.OwnerUserId == ActiveUserId)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .ToListAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Görünen aralıktaki örnekleri yeniden okur.</summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        Notify();

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var query = scope.ServiceProvider.GetRequiredService<CalendarQueryService>();

            // Arama ızgarada süzmez, vurgular: eşleşen etkinliği bağlamı içinde
            // görmek, bağlamsız bir liste görmekten daha kullanışlı. Bu yüzden
            // sorguya arama terimi geçilmez.
            var filter = new OccurrenceFilter
            {
                CalendarIds = [.. VisibleCalendars.Select(c => c.Id)],
                CategoryIds = View.CategoryFilter,
            };

            _occurrences = await query
                .GetOccurrencesAsync(ActiveUserId, RangeStartUtc, RangeEndUtc, filter, ct)
                .ConfigureAwait(false);

            var schedule = scope.ServiceProvider.GetRequiredService<WorkScheduleService>();
            _schedules = await schedule
                .GetRangeAsync(ActiveUserId, View.RangeStart, View.RangeEnd, ct)
                .ConfigureAwait(false);

            _tasks = await scope.ServiceProvider.GetRequiredService<TaskService>()
                .GetForDateRangeAsync(ActiveUserId, View.RangeStart, View.RangeEnd, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            IsLoading = false;
            Notify();
        }
    }

    public Instant RangeStartUtc => timeZones.ToInstant(View.RangeStart.AtMidnight(), ZoneId);

    public Instant RangeEndUtc => timeZones.ToInstant(View.RangeEnd.AtMidnight(), ZoneId);

    // ------------------------------------------------------------------
    // Görünüm değişiklikleri
    // ------------------------------------------------------------------

    /// <summary>Görünümü değiştirir ve gerekiyorsa verileri yeniden okur.</summary>
    public async Task SetViewAsync(CalendarViewState view, CancellationToken ct = default)
    {
        var rangeChanged = view.RangeStart != View.RangeStart
                        || view.RangeEnd != View.RangeEnd
                        || !view.HiddenCalendarIds.SequenceEqual(View.HiddenCalendarIds)
                        || !view.CategoryFilter.SequenceEqual(View.CategoryFilter)
                        || view.SearchTerm != View.SearchTerm;

        View = view;

        if (rangeChanged) await ReloadAsync(ct).ConfigureAwait(false);
        else Notify();
    }

    public Task ShiftAsync(int steps, CancellationToken ct = default)
        => SetViewAsync(View.Shift(steps), ct);

    public Task GoToTodayAsync(CancellationToken ct = default)
        => SetViewAsync(View.GoTo(Today), ct);

    public Task GoToAsync(LocalDate date, CancellationToken ct = default)
        => SetViewAsync(View.GoTo(date), ct);

    public Task ToggleCalendarAsync(Guid calendarId, CancellationToken ct = default)
    {
        var hidden = View.HiddenCalendarIds.ToList();
        if (!hidden.Remove(calendarId)) hidden.Add(calendarId);

        return SetViewAsync(View with { HiddenCalendarIds = hidden }, ct);
    }

    public Task ToggleCategoryAsync(Guid categoryId, CancellationToken ct = default)
    {
        var selected = View.CategoryFilter.ToList();
        if (!selected.Remove(categoryId)) selected.Add(categoryId);

        return SetViewAsync(View with { CategoryFilter = selected }, ct);
    }

    // ------------------------------------------------------------------
    // Tatiller
    // ------------------------------------------------------------------

    /// <summary>Görünen aralıktaki resmi tatiller, tarihe göre gruplanmış.</summary>
    public ILookup<LocalDate, Holiday> VisibleHolidays
        => holidays.InRange(View.RangeStart, View.RangeEnd.PlusDays(-1)).ToLookup(h => h.Date);

    public bool IsDayOff(LocalDate date) => holidays.IsDayOff(date);

    /// <summary>
    /// Günün mesai düzeni. Yüklenmemiş bir gün istenirse makul bir varsayılan
    /// döner; görünüm, veri gelmeden de çizilebilmelidir.
    /// </summary>
    public DaySchedule ScheduleFor(LocalDate date)
        => _schedules.TryGetValue(date, out var schedule)
            ? schedule
            : new DaySchedule(date,
                IsWorkingDay: date.DayOfWeek <= IsoDayOfWeek.Friday,
                Start: new LocalTime(9, 0),
                End: new LocalTime(18, 0),
                BreakStart: null,
                BreakEnd: null,
                Location: WorkLocation.Unspecified,
                LocationNote: null,
                HolidayName: null);

    /// <summary>Bir günün çalışma konumunu ayarlar ve görünümü tazeler.</summary>
    public async Task SetWorkLocationAsync(
        LocalDate date, WorkLocation location, string? note = null, CancellationToken ct = default)
    {
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<WorkScheduleService>();
            await service.SetLocationAsync(ActiveUserId, date, location, note, ct)
                .ConfigureAwait(false);
        }

        await ReloadAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Bildirimler
    // ------------------------------------------------------------------

    /// <summary>Yıkıcı bir işlemden sonra geri alma şeridini gösterir.</summary>
    public void ShowUndo(Guid operationId, string message)
    {
        PendingUndo = new UndoPrompt(operationId, message);
        StatusMessage = null;
        Notify();
    }

    public void ShowStatus(string message)
    {
        StatusMessage = message;
        Notify();
    }

    public void DismissNotifications()
    {
        PendingUndo = null;
        StatusMessage = null;
        Notify();
    }

    /// <summary>Bekleyen işlemi geri alır ve görünümü tazeler.</summary>
    public async Task UndoAsync(CancellationToken ct = default)
    {
        if (PendingUndo is not { } prompt) return;

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var undo = scope.ServiceProvider.GetRequiredService<UndoService>();
            await undo.UndoAsync(prompt.OperationId, ActiveUserId, ct).ConfigureAwait(false);
        }

        PendingUndo = null;
        StatusMessage = "Geri alındı.";
        await ReloadAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Yardımcılar
    // ------------------------------------------------------------------

    /// <summary>Arama etkin mi.</summary>
    public bool IsSearching => !string.IsNullOrWhiteSpace(View.SearchTerm);

    /// <summary>
    /// Örnek, aranan ifadeyle eşleşiyor mu. Karartılmış örnekler hiçbir zaman
    /// eşleşmez: içeriklerini göremeyen biri onların içinde arama da yapamaz.
    /// </summary>
    public bool MatchesSearch(EventOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);

        if (!IsSearching) return true;
        if (!occurrence.CanSeeDetails) return false;

        return Takvim.Core.Text.TurkishText.Contains(occurrence.Source.SearchText, View.SearchTerm);
    }

    /// <summary>Görünen aralıkta arama kaç örnekle eşleşti.</summary>
    public int SearchMatchCount => IsSearching ? _occurrences.Count(MatchesSearch) : 0;

    /// <summary>Takvimin rengini verir; etkinliğin kendi rengi varsa o öne geçer.</summary>
    public string ColorOf(EventOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);

        if (!string.IsNullOrWhiteSpace(occurrence.Source.Color)) return occurrence.Source.Color;

        return _calendars.FirstOrDefault(c => c.Id == occurrence.Source.CalendarId)?.Color ?? "peacock";
    }

    public Calendar? CalendarOf(Guid calendarId) => _calendars.FirstOrDefault(c => c.Id == calendarId);

    /// <summary>Aktif kullanıcının yazabildiği takvimler.</summary>
    public IEnumerable<Calendar> WritableCalendars
        => _calendars.Where(c => !c.IsReadOnly && c.OwnerUserId == ActiveUserId);

    /// <summary>Yeni etkinliklerin varsayılan olarak ekleneceği takvim.</summary>
    public Calendar? DefaultCalendar
        => WritableCalendars.FirstOrDefault(c => c.Kind == CalendarKind.Personal)
        ?? WritableCalendars.FirstOrDefault();

    /// <summary>Takvim başkasına aitse sahibinin adı; kendisininse null.</summary>
    public string? OwnerNameOf(Calendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        return calendar.OwnerUserId == ActiveUserId ? null : calendar.Owner?.DisplayName;
    }

    /// <summary>Belirli bir güne düşen zamanlı örnekler.</summary>
    public IEnumerable<EventOccurrence> TimedOn(LocalDate date)
        => _occurrences.Where(o => !o.IsAllDay
                                && o.StartLocal.Date <= date
                                && o.EndLocal > date.AtMidnight()
                                && o.StartLocal < date.PlusDays(1).AtMidnight());

    /// <summary>Belirli bir güne düşen tüm gün ve çok günlü örnekler.</summary>
    public IEnumerable<EventOccurrence> AllDayOn(LocalDate date)
        => _occurrences.Where(o => o.IsAllDay
                                && o.StartLocal.Date <= date
                                && o.EndLocal.Date > date);

    /// <summary>Bir güne düşen tüm örnekler; ay ve yıl görünümleri bunu kullanır.</summary>
    public IEnumerable<EventOccurrence> On(LocalDate date)
        => _occurrences.Where(o => o.StartLocal.Date <= date
                                && (o.IsAllDay ? o.EndLocal.Date > date : o.EndLocal > date.AtMidnight())
                                && o.StartLocal < date.PlusDays(1).AtMidnight());

    private void Notify() => Changed?.Invoke();
}
