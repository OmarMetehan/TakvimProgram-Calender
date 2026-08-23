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
    private readonly List<Category> _categories = [];
    private List<EventOccurrence> _occurrences = [];

    /// <summary>Görünüm ya da veri değiştiğinde tetiklenir.</summary>
    public event Action? Changed;

    // ------------------------------------------------------------------
    // Durum
    // ------------------------------------------------------------------

    public CalendarViewState View { get; private set; } = new();

    public IReadOnlyList<Calendar> Calendars => _calendars;
    public IReadOnlyList<Category> Categories => _categories;
    public IReadOnlyList<EventOccurrence> Occurrences => _occurrences;

    public bool IsLoading { get; private set; }
    public UndoPrompt? PendingUndo { get; private set; }
    public string? StatusMessage { get; private set; }

    /// <summary>Kullanıcının zaman dilimi. Tüm görünümler bu dilimde çizilir.</summary>
    public string ZoneId { get; private set; } = TimeZoneService.DefaultZoneId;

    public LocalDate Today => timeZones.TodayIn(ZoneId, clock);

    public LocalDateTime Now => timeZones.ToLocal(clock.GetCurrentInstant(), ZoneId);

    /// <summary>Kenar çubuğunda işaretli, yani ızgarada çizilen takvimler.</summary>
    public IEnumerable<Calendar> VisibleCalendars
        => _calendars.Where(c => !View.HiddenCalendarIds.Contains(c.Id));

    // ------------------------------------------------------------------
    // Yükleme
    // ------------------------------------------------------------------

    /// <summary>Takvimleri ve kategorileri okur. Uygulama açılışında bir kez çağrılır.</summary>
    public async Task InitializeAsync(CalendarViewState view, CancellationToken ct = default)
    {
        View = view;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TakvimDbContext>();

        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == CalendarBootstrapper.LocalUserId, ct)
            .ConfigureAwait(false);

        if (user is not null && timeZones.IsKnown(user.TimeZoneId)) ZoneId = user.TimeZoneId;

        _calendars.Clear();
        _calendars.AddRange(await db.Calendars
            .AsNoTracking()
            .Where(c => c.DeletedAt == null)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .ToListAsync(ct).ConfigureAwait(false));

        _categories.Clear();
        _categories.AddRange(await db.Categories
            .AsNoTracking()
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .ToListAsync(ct).ConfigureAwait(false));

        await ReloadAsync(ct).ConfigureAwait(false);
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

            var filter = new OccurrenceFilter
            {
                CalendarIds = [.. VisibleCalendars.Select(c => c.Id)],
                CategoryIds = View.CategoryFilter,
                SearchTerm = View.SearchTerm,
            };

            _occurrences = await query
                .GetOccurrencesAsync(RangeStartUtc, RangeEndUtc, filter, ct)
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
            await undo.UndoAsync(prompt.OperationId, CalendarBootstrapper.LocalUserId, ct).ConfigureAwait(false);
        }

        PendingUndo = null;
        StatusMessage = "Geri alındı.";
        await ReloadAsync(ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // Yardımcılar
    // ------------------------------------------------------------------

    /// <summary>Takvimin rengini verir; etkinliğin kendi rengi varsa o öne geçer.</summary>
    public string ColorOf(EventOccurrence occurrence)
    {
        ArgumentNullException.ThrowIfNull(occurrence);

        if (!string.IsNullOrWhiteSpace(occurrence.Source.Color)) return occurrence.Source.Color;

        return _calendars.FirstOrDefault(c => c.Id == occurrence.Source.CalendarId)?.Color ?? "peacock";
    }

    public Calendar? CalendarOf(Guid calendarId) => _calendars.FirstOrDefault(c => c.Id == calendarId);

    /// <summary>Yeni etkinliklerin varsayılan olarak ekleneceği takvim.</summary>
    public Calendar? DefaultCalendar
        => _calendars.FirstOrDefault(c => c.Kind == CalendarKind.Personal && !c.IsReadOnly)
        ?? _calendars.FirstOrDefault(c => !c.IsReadOnly);

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
