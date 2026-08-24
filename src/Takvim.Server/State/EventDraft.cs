using System.Globalization;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Localization;
using Takvim.Core.Recurrence;
using Takvim.Data.Services;

namespace Takvim.Server.State;

/// <summary>
/// Düzenleyicinin üzerinde çalıştığı taslak. Varlığın kendisi değil, formun hâlidir:
/// tarih ve saat alanları ayrı tutulur, çünkü kullanıcı ikisini bağımsız değiştirir.
/// </summary>
public sealed class EventDraft
{
    /// <summary>Yeni etkinlikte null.</summary>
    public Guid? EventId { get; set; }

    /// <summary>Tekrarlayan bir örneği düzenlerken o örneğin özgün başlangıcı.</summary>
    public LocalDateTime? RecurrenceId { get; set; }

    /// <summary>Düzenlenen kayıt bir seriye mi ait; kapsam sorusunun sorulup sorulmayacağını belirler.</summary>
    public bool BelongsToSeries { get; set; }

    public bool IsNew => EventId is null;

    public Guid CalendarId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? DescriptionHtml { get; set; }
    public string? AgendaText { get; set; }
    public string? PrivateNotes { get; set; }
    public string? LocationText { get; set; }
    public string? OnlineMeetingUrl { get; set; }
    public string? OnlineMeetingProvider { get; set; }
    public string? Color { get; set; }

    public bool IsAllDay { get; set; }

    /// <summary>ISO biçiminde (yyyy-MM-dd); tarayıcının tarih seçicisi bu biçimi kullanır.</summary>
    public string StartDate { get; set; } = "";
    public string StartTime { get; set; } = "09:00";
    public string EndDate { get; set; } = "";
    public string EndTime { get; set; } = "10:00";

    public string? StartTimeZoneId { get; set; }
    public string? EndTimeZoneId { get; set; }

    /// <summary>Başlangıç ve bitiş için ayrı zaman dilimi gösterilsin mi.</summary>
    public bool SplitTimeZones { get; set; }

    public Availability Availability { get; set; } = Availability.Busy;
    public EventVisibility Visibility { get; set; } = EventVisibility.Default;
    public bool IsForwardable { get; set; } = true;

    public RecurrencePreset RecurrencePreset { get; set; } = RecurrencePreset.None;
    public string? CustomRecurrenceRule { get; set; }

    /// <summary>Resmi tatile denk gelen örneklere ne olacağı.</summary>
    public HolidayBehavior HolidayBehavior { get; set; } = HolidayBehavior.Include;

    public List<Guid> CategoryIds { get; set; } = [];
    public List<ReminderInput> Reminders { get; set; } = [];

    /// <summary>Davetliler. Boşsa etkinlik kişiseldir, toplantı değildir.</summary>
    public List<AttendeeDraft> Attendees { get; set; } = [];

    /// <summary>Etkinliğin organizatörü; katılımcı listesinde ayrı gösterilir.</summary>
    public Guid? OrganizerUserId { get; set; }

    // ------------------------------------------------------------------
    // Kurulum
    // ------------------------------------------------------------------

    /// <summary>Boş bir zaman aralığından yeni taslak kurar.</summary>
    public static EventDraft ForNewEvent(
        Guid calendarId, LocalDateTime start, LocalDateTime end, bool allDay, string zoneId, int? defaultReminder)
    {
        var draft = new EventDraft
        {
            CalendarId = calendarId,
            IsAllDay = allDay,
            StartTimeZoneId = zoneId,
            EndTimeZoneId = zoneId,
        };

        draft.SetStart(start);
        draft.SetEnd(end);

        if (defaultReminder is { } minutes) draft.Reminders.Add(new ReminderInput(minutes));

        return draft;
    }

    /// <summary>Var olan bir örnekten taslak kurar.</summary>
    public static EventDraft FromOccurrence(EventOccurrence occurrence, string zoneId)
    {
        ArgumentNullException.ThrowIfNull(occurrence);

        var source = occurrence.Source;
        var seriesRoot = source.SeriesId is not null || source.RecurrenceRule is not null;

        var draft = new EventDraft
        {
            EventId = occurrence.SeriesRootId,
            RecurrenceId = occurrence.RecurrenceId,
            BelongsToSeries = seriesRoot,
            CalendarId = source.CalendarId,
            Title = source.Title,
            DescriptionHtml = source.DescriptionHtml,
            AgendaText = source.AgendaText,
            PrivateNotes = source.PrivateNotes,
            LocationText = source.LocationText,
            OnlineMeetingUrl = source.OnlineMeetingUrl,
            OnlineMeetingProvider = source.OnlineMeetingProvider,
            Color = source.Color,
            IsAllDay = source.IsAllDay,
            StartTimeZoneId = source.StartTimeZoneId ?? zoneId,
            EndTimeZoneId = source.EndTimeZoneId ?? source.StartTimeZoneId ?? zoneId,
            SplitTimeZones = source.EndTimeZoneId is not null
                          && source.EndTimeZoneId != source.StartTimeZoneId,
            Availability = source.Availability,
            Visibility = source.Visibility,
            IsForwardable = source.IsForwardable,
            CategoryIds = [.. source.Categories.Select(c => c.CategoryId)],
            Reminders = [.. source.Reminders.Select(r => new ReminderInput(r.MinutesBefore, r.Channel))],
            Attendees = [.. source.Attendees.Select(AttendeeDraft.From)],
            OrganizerUserId = source.OrganizerUserId,
            CustomRecurrenceRule = source.RecurrenceRule,
            HolidayBehavior = source.HolidayBehavior,
            RecurrencePreset = source.RecurrenceRule is null ? RecurrencePreset.None : RecurrencePreset.Custom,
        };

        // Düzenlenen, serinin bu örneğidir; kullanıcı gördüğü saati düzenler.
        draft.SetStart(occurrence.StartLocal);
        draft.SetEnd(occurrence.EndLocal);

        return draft;
    }

    public void SetStart(LocalDateTime value)
    {
        StartDate = value.Date.ToString("uuuu-MM-dd", CultureInfo.InvariantCulture);
        StartTime = value.TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    public void SetEnd(LocalDateTime value)
    {
        // Tüm gün etkinliğinin bitişi dışlayıcıdır; kullanıcıya son gün gösterilir.
        var display = IsAllDay ? value.Date.PlusDays(-1).AtMidnight() : value;

        EndDate = display.Date.ToString("uuuu-MM-dd", CultureInfo.InvariantCulture);
        EndTime = display.TimeOfDay.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------
    // Okuma
    // ------------------------------------------------------------------

    public LocalDateTime Start => Compose(StartDate, StartTime, IsAllDay);

    /// <summary>Kayda yazılacak dışlayıcı bitiş.</summary>
    public LocalDateTime End
    {
        get
        {
            var value = Compose(EndDate, EndTime, IsAllDay);
            // Tüm gün: kullanıcı son günü seçer, kayda ertesi günün gece yarısı yazılır.
            return IsAllDay ? value.Date.PlusDays(1).AtMidnight() : value;
        }
    }

    public string? EffectiveRecurrenceRule => RecurrencePreset switch
    {
        RecurrencePreset.None => null,
        RecurrencePreset.Custom => string.IsNullOrWhiteSpace(CustomRecurrenceRule) ? null : CustomRecurrenceRule,
        _ => RecurrenceRuleBuilder.FromPreset(RecurrencePreset, Start.Date),
    };

    public string RecurrenceDescription
        => RecurrenceRuleBuilder.Describe(EffectiveRecurrenceRule, Start.Date);

    /// <summary>Kaydetmeden önce görülen sorunlar. Boşsa kayıt yapılabilir.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (CalendarId == Guid.Empty) problems.Add("Bir takvim seçin.");

        var duplicate = Attendees
            .GroupBy(a => a.Email.Trim().ToLowerInvariant())
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null) problems.Add($"Aynı kişi iki kez eklenmiş: {duplicate.Key}");

        var invalid = Attendees.FirstOrDefault(a => !a.Email.Contains('@', StringComparison.Ordinal));
        if (invalid is not null) problems.Add($"Geçersiz e-posta: {invalid.Email}");
        if (End <= Start) problems.Add("Bitiş, başlangıçtan sonra olmalı.");

        if (!RecurrenceRuleBuilder.TryParse(EffectiveRecurrenceRule, out var error) && error is not null)
            problems.Add(error);

        if (!string.IsNullOrWhiteSpace(OnlineMeetingUrl)
            && !Uri.TryCreate(OnlineMeetingUrl, UriKind.Absolute, out _))
        {
            problems.Add("Toplantı bağlantısı geçerli bir adres değil.");
        }

        return problems;
    }

    /// <summary>Katılımcıları servise verilecek biçime çevirir.</summary>
    public IReadOnlyList<AttendeeInput> ToAttendeeInputs()
        => [.. Attendees.Select(a => new AttendeeInput(
            a.Email, a.DisplayName, a.UserId, a.Role, a.CanEdit, a.CanInviteOthers, a.CanSeeGuestList))];

    public EventInput ToInput(Guid actorUserId) => new()
    {
        CalendarId = CalendarId,
        Title = string.IsNullOrWhiteSpace(Title) ? "(başlıksız)" : Title.Trim(),
        DescriptionHtml = DescriptionHtml,
        AgendaText = string.IsNullOrWhiteSpace(AgendaText) ? null : AgendaText.Trim(),
        PrivateNotes = string.IsNullOrWhiteSpace(PrivateNotes) ? null : PrivateNotes.Trim(),
        LocationText = string.IsNullOrWhiteSpace(LocationText) ? null : LocationText.Trim(),
        OnlineMeetingUrl = string.IsNullOrWhiteSpace(OnlineMeetingUrl) ? null : OnlineMeetingUrl.Trim(),
        OnlineMeetingProvider = OnlineMeetingProvider,
        Color = Color,
        StartLocal = Start,
        EndLocal = End,
        StartTimeZoneId = IsAllDay ? null : StartTimeZoneId,
        EndTimeZoneId = IsAllDay ? null : (SplitTimeZones ? EndTimeZoneId : StartTimeZoneId),
        IsAllDay = IsAllDay,
        Availability = Availability,
        Visibility = Visibility,
        IsForwardable = IsForwardable,
        RecurrenceRule = EffectiveRecurrenceRule,
        HolidayBehavior = HolidayBehavior,
        CategoryIds = CategoryIds,
        Reminders = Reminders,
        ActorUserId = actorUserId,
    };

    /// <summary>Tüm gün seçimi açılıp kapandığında saatleri makul değerlere çeker.</summary>
    public void OnAllDayChanged()
    {
        if (IsAllDay)
        {
            // Bitiş günü, başlangıç gününden önce kalmasın.
            if (string.CompareOrdinal(EndDate, StartDate) < 0) EndDate = StartDate;
        }
        else
        {
            StartTime = "09:00";
            EndTime = "10:00";
            EndDate = StartDate;
        }
    }

    /// <summary>Başlangıç değiştiğinde bitişi aynı süreyi koruyacak biçimde kaydırır.</summary>
    public void ShiftEndWithStart(LocalDateTime previousStart)
    {
        var length = End - previousStart;
        if (length.Days < 0) return;

        SetEnd(Start + length);
    }

    private static LocalDateTime Compose(string date, string time, bool allDay)
    {
        if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsedDate))
        {
            parsedDate = DateTime.Today;
        }

        var day = LocalDate.FromDateTime(parsedDate);
        if (allDay) return day.AtMidnight();

        return TurkishFormat.TryParseTime(time, out var parsedTime)
            ? day + parsedTime
            : day.At(new LocalTime(9, 0));
    }
}

/// <summary>
/// Düzenleyicideki katılımcı satırı. Var olan bir katılımcının yanıtını da
/// taşır; liste yeniden kurulduğunda yanıtlar kaybolmaz.
/// </summary>
public sealed class AttendeeDraft
{
    public Guid? UserId { get; set; }
    public required string Email { get; set; }
    public required string DisplayName { get; set; }

    public AttendeeRole Role { get; set; } = AttendeeRole.Required;

    public bool CanEdit { get; set; }
    public bool CanInviteOthers { get; set; }
    public bool CanSeeGuestList { get; set; } = true;

    /// <summary>Var olan bir katılımcıysa verdiği yanıt; yeni eklenende beklemede.</summary>
    public ResponseStatus Response { get; set; } = ResponseStatus.NeedsAction;

    public string? ResponseComment { get; set; }

    /// <summary>Bekleyen zaman önerisinin özeti; varsa arayüzde gösterilir.</summary>
    public string? ProposalSummary { get; set; }

    /// <summary>
    /// Yanıt, toplantının şimdiki saatinden başka bir saate verilmiş mi.
    /// Taslak açılırken kaydedilmiş saate göre hesaplanır; kullanıcı formda
    /// saati değiştirirken her tuş vuruşunda yeniden hesaplanmaz.
    /// </summary>
    public bool IsResponseStale { get; set; }

    /// <summary>Yanıtın verildiği saat; eski yanıtlarda ipucu olarak gösterilir.</summary>
    public LocalDateTime? RespondedForStart { get; set; }

    /// <summary>Bu makinede hesabı olmayan biri: davet ona ulaşmaz.</summary>
    public bool IsExternal => UserId is null;

    public static AttendeeDraft From(Attendee attendee)
    {
        ArgumentNullException.ThrowIfNull(attendee);

        return new AttendeeDraft
        {
            UserId = attendee.UserId,
            Email = attendee.Email,
            DisplayName = attendee.DisplayName,
            Role = attendee.Role,
            CanEdit = attendee.CanEdit,
            CanInviteOthers = attendee.CanInviteOthers,
            CanSeeGuestList = attendee.CanSeeGuestList,
            Response = attendee.Response,
            ResponseComment = attendee.ResponseComment,
            IsResponseStale = attendee.IsResponseStale,
            RespondedForStart = attendee.RespondedForStartLocal,
            ProposalSummary = attendee.ProposedStartLocal is { } start
                ? $"{start:dd.MM.yyyy HH:mm}"
                : null,
        };
    }
}
