using System.Globalization;
using NodaTime;
using NodaTime.Text;
using Takvim.Core.Domain;

namespace Takvim.Data.Services;

/// <summary>
/// Bir etkinlik satırının tam kopyası. Değişiklik günlüğünde saklanır ve
/// geri alma bunu geri yazar.
/// <para>
/// NodaTime tipleri yerine metin kullanılır: günlük kaydı yıllarca saklanacağı
/// ve denetim amacıyla elle okunabileceği için, biçimin kütüphane sürümünden
/// bağımsız ve gözle okunur olması gerekir.
/// </para>
/// </summary>
public sealed record EventSnapshot
{
    private static readonly LocalDateTimePattern LocalPattern =
        LocalDateTimePattern.CreateWithInvariantCulture("uuuu-MM-ddTHH:mm:ss");

    private static readonly InstantPattern InstantPatternUtc =
        InstantPattern.CreateWithInvariantCulture("uuuu-MM-ddTHH:mm:ss'Z'");

    public Guid Id { get; init; }
    public Guid CalendarId { get; init; }
    public string Uid { get; init; } = "";

    public string StartLocal { get; init; } = "";
    public string EndLocal { get; init; } = "";
    public string? StartTimeZoneId { get; init; }
    public string? EndTimeZoneId { get; init; }
    public bool IsAllDay { get; init; }
    public string StartUtc { get; init; } = "";
    public string EndUtc { get; init; } = "";

    public string Title { get; init; } = "";
    public string? DescriptionHtml { get; init; }
    public string? LocationText { get; init; }
    public string? OnlineMeetingUrl { get; init; }
    public string? OnlineMeetingProvider { get; init; }
    public string? Color { get; init; }
    public string SearchText { get; init; } = "";

    public Availability Availability { get; init; }
    public EventVisibility Visibility { get; init; }
    public EventStatus Status { get; init; }
    public string? CancellationReason { get; init; }
    public bool IsForwardable { get; init; }
    public Guid? OrganizerUserId { get; init; }

    public string? RecurrenceRule { get; init; }
    public HolidayBehavior HolidayBehavior { get; init; }
    public string? ExDates { get; init; }
    public string? RDates { get; init; }
    public string? SeriesEndUtc { get; init; }
    public string? RecurrenceId { get; init; }
    public Guid? SeriesId { get; init; }

    public int Sequence { get; init; }
    public string ETag { get; init; } = "";
    public string LastModifiedUtc { get; init; } = "";
    public string? SourceId { get; init; }
    public string? RemoteId { get; init; }

    public string CreatedAt { get; init; } = "";
    public string UpdatedAt { get; init; } = "";
    public string? DeletedAt { get; init; }

    public IReadOnlyList<Guid> CategoryIds { get; init; } = [];
    public IReadOnlyList<ReminderSnapshot> Reminders { get; init; } = [];

    public static EventSnapshot From(Event ev)
    {
        ArgumentNullException.ThrowIfNull(ev);

        return new EventSnapshot
        {
            Id = ev.Id,
            CalendarId = ev.CalendarId,
            Uid = ev.Uid,
            StartLocal = LocalPattern.Format(ev.StartLocal),
            EndLocal = LocalPattern.Format(ev.EndLocal),
            StartTimeZoneId = ev.StartTimeZoneId,
            EndTimeZoneId = ev.EndTimeZoneId,
            IsAllDay = ev.IsAllDay,
            StartUtc = InstantPatternUtc.Format(ev.StartUtc),
            EndUtc = InstantPatternUtc.Format(ev.EndUtc),
            Title = ev.Title,
            DescriptionHtml = ev.DescriptionHtml,
            LocationText = ev.LocationText,
            OnlineMeetingUrl = ev.OnlineMeetingUrl,
            OnlineMeetingProvider = ev.OnlineMeetingProvider,
            Color = ev.Color,
            SearchText = ev.SearchText,
            Availability = ev.Availability,
            Visibility = ev.Visibility,
            Status = ev.Status,
            CancellationReason = ev.CancellationReason,
            IsForwardable = ev.IsForwardable,
            OrganizerUserId = ev.OrganizerUserId,
            RecurrenceRule = ev.RecurrenceRule,
            HolidayBehavior = ev.HolidayBehavior,
            ExDates = ev.ExDates,
            RDates = ev.RDates,
            SeriesEndUtc = ev.SeriesEndUtc is { } end ? InstantPatternUtc.Format(end) : null,
            RecurrenceId = ev.RecurrenceId is { } rid ? LocalPattern.Format(rid) : null,
            SeriesId = ev.SeriesId,
            Sequence = ev.Sequence,
            ETag = ev.ETag,
            LastModifiedUtc = InstantPatternUtc.Format(ev.LastModifiedUtc),
            SourceId = ev.SourceId,
            RemoteId = ev.RemoteId,
            CreatedAt = ev.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            UpdatedAt = ev.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
            DeletedAt = ev.DeletedAt?.ToString("O", CultureInfo.InvariantCulture),
            CategoryIds = [.. ev.Categories.Select(c => c.CategoryId)],
            Reminders = [.. ev.Reminders.Select(r => new ReminderSnapshot(r.MinutesBefore, r.Channel))],
        };
    }

    /// <summary>Anlık görüntüyü var olan bir satıra geri yazar. Geri alma bunu kullanır.</summary>
    public void ApplyTo(Event ev)
    {
        ArgumentNullException.ThrowIfNull(ev);

        ev.CalendarId = CalendarId;
        ev.Uid = Uid;
        ev.StartLocal = LocalPattern.Parse(StartLocal).Value;
        ev.EndLocal = LocalPattern.Parse(EndLocal).Value;
        ev.StartTimeZoneId = StartTimeZoneId;
        ev.EndTimeZoneId = EndTimeZoneId;
        ev.IsAllDay = IsAllDay;
        ev.StartUtc = InstantPatternUtc.Parse(StartUtc).Value;
        ev.EndUtc = InstantPatternUtc.Parse(EndUtc).Value;
        ev.Title = Title;
        ev.DescriptionHtml = DescriptionHtml;
        ev.LocationText = LocationText;
        ev.OnlineMeetingUrl = OnlineMeetingUrl;
        ev.OnlineMeetingProvider = OnlineMeetingProvider;
        ev.Color = Color;
        ev.SearchText = SearchText;
        ev.Availability = Availability;
        ev.Visibility = Visibility;
        ev.Status = Status;
        ev.CancellationReason = CancellationReason;
        ev.IsForwardable = IsForwardable;
        ev.OrganizerUserId = OrganizerUserId;
        ev.RecurrenceRule = RecurrenceRule;
        ev.HolidayBehavior = HolidayBehavior;
        ev.ExDates = ExDates;
        ev.RDates = RDates;
        ev.SeriesEndUtc = SeriesEndUtc is null ? null : InstantPatternUtc.Parse(SeriesEndUtc).Value;
        ev.RecurrenceId = RecurrenceId is null ? null : LocalPattern.Parse(RecurrenceId).Value;
        ev.SeriesId = SeriesId;
        ev.Sequence = Sequence;
        ev.ETag = ETag;
        ev.LastModifiedUtc = InstantPatternUtc.Parse(LastModifiedUtc).Value;
        ev.SourceId = SourceId;
        ev.RemoteId = RemoteId;
        ev.CreatedAt = DateTimeOffset.Parse(CreatedAt, CultureInfo.InvariantCulture);
        ev.UpdatedAt = DateTimeOffset.Parse(UpdatedAt, CultureInfo.InvariantCulture);
        ev.DeletedAt = DeletedAt is null ? null : DateTimeOffset.Parse(DeletedAt, CultureInfo.InvariantCulture);
    }

    /// <summary>Anlık görüntüden yeni bir satır kurar. Silinmiş etkinliği geri getirmek için.</summary>
    public Event ToEvent()
    {
        var ev = new Event { Id = Id, Uid = Uid, ETag = ETag };
        ApplyTo(ev);
        return ev;
    }
}

public sealed record ReminderSnapshot(int MinutesBefore, ReminderChannel Channel);
