using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using Takvim.Core.Domain;
using Takvim.Core.Recurrence;
using Takvim.Core.Time;
using Takvim.Data;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// Her test için bellek içi ama <b>gerçek</b> bir SQLite veritabanı kurar.
/// EF Core'un InMemory sağlayıcısı yerine bunun kullanılmasının nedeni,
/// NodaTime dönüştürücülerinin ve indekslerin de sınanmasıdır: InMemory
/// sağlayıcısı bunların hiçbirini çalıştırmaz.
/// </summary>
internal sealed class TestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public TakvimDbContext Db { get; }
    public EventService Events { get; }
    public CalendarQueryService Query { get; }
    public UndoService Undo { get; }
    public ReminderService Reminders { get; }
    public TimeZoneService Zones { get; } = new();
    public RecurrenceExpander Expander { get; }
    public FakeClock Clock { get; } = new(Instant.FromUtc(2026, 1, 1, 9, 0));

    public Guid UserId { get; }
    public Guid CalendarId { get; }

    public TestDatabase()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<TakvimDbContext>()
            .UseSqlite(_connection)
            .Options;

        Db = new TakvimDbContext(options);
        Db.Database.EnsureCreated();

        Expander = new RecurrenceExpander(Zones);
        Events = new EventService(Db, Zones, Expander, Clock);
        Query = new CalendarQueryService(Db, Expander);
        Undo = new UndoService(Db);
        Reminders = new ReminderService(Db, Expander, Clock);

        var user = new User { DisplayName = "Test Kullanıcı", Email = "test@ornek.local" };
        var calendar = new Calendar { Name = "Kişisel", OwnerUserId = user.Id };
        Db.Users.Add(user);
        Db.Calendars.Add(calendar);
        Db.SaveChanges();

        UserId = user.Id;
        CalendarId = calendar.Id;
    }

    public EventInput Input(
        string start,
        string end,
        string title = "Toplantı",
        string? rrule = null,
        bool allDay = false,
        string tz = "Europe/Istanbul",
        IReadOnlyList<ReminderInput>? reminders = null)
        => new()
        {
            CalendarId = CalendarId,
            Title = title,
            StartLocal = Parse(start),
            EndLocal = Parse(end),
            StartTimeZoneId = allDay ? null : tz,
            EndTimeZoneId = allDay ? null : tz,
            IsAllDay = allDay,
            RecurrenceRule = rrule,
            Reminders = reminders ?? [],
            ActorUserId = UserId,
        };

    public static LocalDateTime Parse(string value)
        => LocalDateTime.FromDateTime(DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture));

    public Instant Utc(string value, string tz = "Europe/Istanbul") => Zones.ToInstant(Parse(value), tz);

    /// <summary>Bir aralıktaki örneklerin başlangıçlarını okunur biçimde döker.</summary>
    public async Task<string[]> StartsAsync(string from, string to)
    {
        var occurrences = await Query.GetOccurrencesAsync(Utc(from), Utc(to)).ConfigureAwait(false);
        return [.. occurrences
            .OrderBy(o => o.StartUtc)
            .Select(o => o.StartLocal.ToString("uuuu-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture))];
    }

    public async Task<string[]> TitlesAsync(string from, string to)
    {
        var occurrences = await Query.GetOccurrencesAsync(Utc(from), Utc(to)).ConfigureAwait(false);
        return [.. occurrences.OrderBy(o => o.StartUtc).Select(o => o.Source.Title)];
    }

    /// <summary>EF'in izleme önbelleğini boşaltır; yazma sonrası okumanın gerçekten diskten geldiğini garantiler.</summary>
    public void Detach() => Db.ChangeTracker.Clear();

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }
}
