using System.Globalization;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Time;
using IcalCalendar = Ical.Net.Calendar;

namespace Takvim.Core.Ics;

/// <summary>İçe aktarma sonucu: kurulmuş satırlar ve atlanan kayıtların gerekçesi.</summary>
public sealed record IcsImportResult(
    IReadOnlyList<Event> Events,
    IReadOnlyList<string> Warnings)
{
    public int EventCount => Events.Count(e => e.SeriesId is null && e.RecurrenceId is null);
    public int ExceptionCount => Events.Count(e => e.RecurrenceId is not null);
}

/// <summary>
/// iCalendar (ICS) dönüştürücüsü.
/// <para>
/// Veri modelimiz RFC 5545'in yapısıyla birebir kurulduğu için dönüşüm sığdır:
/// seri kökü ile istisnaları aynı UID'yi paylaşır, istisnalar RECURRENCE-ID
/// taşır. Bu sayede dışa aktarım kayıpsız, içe aktarım da yeniden yapılandırma
/// gerektirmez.
/// </para>
/// </summary>
public sealed class IcsSerializer(TimeZoneService timeZones)
{
    private const string ProductId = "-//Takvim//Takvim Uygulaması//TR";

    /// <summary>Meşguliyet durumunu ICS'e taşıyan özel alan; standart TRANSP bunu ifade edemez.</summary>
    private const string AvailabilityProperty = "X-TAKVIM-AVAILABILITY";

    // ==================================================================
    // Dışa aktarma
    // ==================================================================

    /// <summary>Etkinlikleri ICS metnine çevirir.</summary>
    /// <param name="events">Seri kökleri, tekil etkinlikler ve istisna satırları bir arada verilebilir.</param>
    /// <param name="calendarName">Alıcı uygulamada görünecek takvim adı.</param>
    public string Export(IEnumerable<Event> events, string? calendarName = null)
    {
        ArgumentNullException.ThrowIfNull(events);

        var calendar = new IcalCalendar { ProductId = ProductId };
        if (!string.IsNullOrWhiteSpace(calendarName))
            calendar.AddProperty("X-WR-CALNAME", calendarName);

        var zonesUsed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var ev in events)
        {
            if (ev.DeletedAt is not null) continue;

            calendar.Events.Add(ToIcal(ev));

            if (ev.StartTimeZoneId is { } startZone) zonesUsed.Add(startZone);
            if (ev.EndTimeZoneId is { } endZone) zonesUsed.Add(endZone);
        }

        // VTIMEZONE bileşenleri olmadan diğer istemciler yerel saatleri çözemez.
        foreach (var zoneId in zonesUsed.Where(timeZones.IsKnown).OrderBy(z => z, StringComparer.Ordinal))
        {
            try { calendar.AddTimeZone(zoneId); }
            catch (ArgumentException) { /* dilim tanımlanamadı: kayıt yine de dışa aktarılır */ }
        }

        return new CalendarSerializer().SerializeToString(calendar) ?? string.Empty;
    }

    private static CalendarEvent ToIcal(Event ev)
    {
        var calendarEvent = new CalendarEvent
        {
            Uid = ev.Uid,
            Summary = ev.Title,
            Description = ev.DescriptionHtml is null ? null : Text.TurkishText.StripHtml(ev.DescriptionHtml),
            Location = ev.LocationText,
            Sequence = ev.Sequence,
            DtStart = ToCalDateTime(ev.StartLocal, ev.StartTimeZoneId, ev.IsAllDay),
            DtEnd = ToCalDateTime(ev.EndLocal, ev.EndTimeZoneId ?? ev.StartTimeZoneId, ev.IsAllDay),
            Created = ToUtcCalDateTime(ev.CreatedAt),
            LastModified = ToUtcCalDateTime(ev.UpdatedAt),
            Status = ev.Status switch
            {
                EventStatus.Cancelled => "CANCELLED",
                EventStatus.Tentative => "TENTATIVE",
                _ => "CONFIRMED",
            },
            // Serbest gösterilen etkinlikler diğer istemcilerde yer kaplamamalıdır.
            Transparency = ev.Availability == Availability.Free ? "TRANSPARENT" : "OPAQUE",
        };

        if (ev.Visibility != EventVisibility.Default)
            calendarEvent.Class = ev.Visibility == EventVisibility.Private ? "PRIVATE" : "PUBLIC";

        // Meşgul/Ofis dışı/Odaklanma ayrımı standartta yoktur; kendi alanımızla taşınır.
        if (ev.Availability is not (Availability.Busy or Availability.Free))
            calendarEvent.AddProperty(AvailabilityProperty, ev.Availability.ToString().ToUpperInvariant());

        if (!string.IsNullOrWhiteSpace(ev.RecurrenceRule))
            calendarEvent.RecurrenceRule = new RecurrencePattern(ev.RecurrenceRule);

        foreach (var date in Recurrence.RecurrenceExpander.ParseDateList(ev.ExDates))
            calendarEvent.ExceptionDates.Add(ToCalDateTime(date, ev.StartTimeZoneId, ev.IsAllDay));

        foreach (var date in Recurrence.RecurrenceExpander.ParseDateList(ev.RDates))
            calendarEvent.RecurrenceDates.Add(ToCalDateTime(date, ev.StartTimeZoneId, ev.IsAllDay));

        if (ev.RecurrenceId is { } recurrenceId)
        {
            calendarEvent.RecurrenceIdentifier = new RecurrenceIdentifier(
                ToCalDateTime(recurrenceId, ev.StartTimeZoneId, ev.IsAllDay), range: null);
        }

        foreach (var reminder in ev.Reminders)
        {
            calendarEvent.Alarms.Add(new Alarm
            {
                Action = "DISPLAY",
                Summary = ev.Title,
                Trigger = new Trigger(Ical.Net.DataTypes.Duration.FromMinutes(-reminder.MinutesBefore)),
            });
        }

        return calendarEvent;
    }

    // ==================================================================
    // İçe aktarma
    // ==================================================================

    /// <summary>ICS metnini etkinlik satırlarına çevirir. Veritabanına yazmaz.</summary>
    /// <param name="ics">Ham ICS içeriği.</param>
    /// <param name="calendarId">Etkinliklerin ekleneceği takvim.</param>
    /// <param name="defaultZoneId">Zaman dilimi belirtmeyen kayıtlar için varsayılan.</param>
    public IcsImportResult Import(string ics, Guid calendarId, string? defaultZoneId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ics);

        var warnings = new List<string>();
        var zone = defaultZoneId ?? TimeZoneService.DefaultZoneId;

        IcalCalendar[] calendars;
        try
        {
            calendars = [.. IcalCalendar.Load<IcalCalendar>(ics)];
        }
#pragma warning disable CA1031 // Dış dosya ayrıştırma sınırı: hiçbir istisna uygulamayı düşürmemeli.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Kullanıcının seçtiği dosya keyfi içerik taşıyabilir. Ayrıştırıcının
            // fırlatabileceği tür kümesi kütüphane sürümüne göre değişir; burada
            // hepsi uyarıya çevrilir ve içe aktarma boş sonuçla döner.
            return new IcsImportResult([], [$"Dosya okunamadı: {ex.Message}"]);
        }

        var parsed = new List<Event>();

        foreach (var calendarEvent in calendars.SelectMany(c => c.Events))
        {
            if (calendarEvent.DtStart is null)
            {
                warnings.Add($"Başlangıcı olmayan kayıt atlandı: {calendarEvent.Summary ?? calendarEvent.Uid}");
                continue;
            }

            try
            {
                parsed.Add(FromIcal(calendarEvent, calendarId, zone));
            }
#pragma warning disable CA1031 // Tek bozuk kayıt, dosyanın tamamının aktarımını engellememeli.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                warnings.Add($"\"{calendarEvent.Summary}\" aktarılamadı: {ex.Message}");
            }
        }

        LinkExceptionsToSeries(parsed, warnings);
        return new IcsImportResult(parsed, warnings);
    }

    private Event FromIcal(CalendarEvent source, Guid calendarId, string defaultZoneId)
    {
        var isAllDay = !source.DtStart!.HasTime;
        var startZone = ResolveZone(source.DtStart.TzId, defaultZoneId);
        var endZone = ResolveZone(source.DtEnd?.TzId, startZone);

        var startLocal = ToLocalDateTime(source.DtStart);
        var endLocal = source.DtEnd is { } dtEnd
            ? ToLocalDateTime(dtEnd)
            // DTEND yoksa RFC 5545: tüm gün etkinliği bir gün, zamanlı etkinlik ise sıfır süre.
            : (isAllDay ? startLocal.PlusDays(1) : startLocal);

        var ev = new Event
        {
            CalendarId = calendarId,
            Uid = string.IsNullOrWhiteSpace(source.Uid) ? $"{Guid.NewGuid():N}@takvim.local" : source.Uid,
            ETag = Guid.NewGuid().ToString("N")[..16],
            Title = source.Summary ?? "(başlıksız)",
            DescriptionHtml = source.Description,
            LocationText = source.Location,
            IsAllDay = isAllDay,
            StartLocal = startLocal,
            EndLocal = endLocal,
            StartTimeZoneId = isAllDay ? null : startZone,
            EndTimeZoneId = isAllDay ? null : endZone,
            Sequence = source.Sequence,
            Status = source.Status?.ToUpperInvariant() switch
            {
                "CANCELLED" => EventStatus.Cancelled,
                "TENTATIVE" => EventStatus.Tentative,
                _ => EventStatus.Confirmed,
            },
            Visibility = source.Class?.ToUpperInvariant() switch
            {
                "PRIVATE" or "CONFIDENTIAL" => EventVisibility.Private,
                "PUBLIC" => EventVisibility.Public,
                _ => EventVisibility.Default,
            },
            Availability = ReadAvailability(source),
            RecurrenceRule = source.RecurrenceRule?.ToString(),
        };

        var exDates = source.ExceptionDates.GetAllDates().Select(ToLocalDateTime).ToList();
        if (exDates.Count > 0)
            ev.ExDates = Recurrence.RecurrenceExpander.FormatDateList(exDates);

        var rDates = source.RecurrenceDates.GetAllDates().Select(ToLocalDateTime).ToList();
        if (rDates.Count > 0)
            ev.RDates = Recurrence.RecurrenceExpander.FormatDateList(rDates);

        if (source.RecurrenceIdentifier?.StartTime is { } recurrenceId)
            ev.RecurrenceId = ToLocalDateTime(recurrenceId);

        foreach (var alarm in source.Alarms)
        {
            if (alarm.Trigger?.Duration is not { } offset) continue;

            var minutes = (int)Math.Round(-offset.ToTimeSpanUnspecified().TotalMinutes);
            ev.Reminders.Add(new Reminder { EventId = ev.Id, MinutesBefore = minutes });
        }

        ev.StartUtc = timeZones.ToInstant(ev.StartLocal, ev.StartTimeZoneId ?? startZone);
        ev.EndUtc = timeZones.ToInstant(ev.EndLocal, ev.EndTimeZoneId ?? endZone);
        ev.LastModifiedUtc = ev.StartUtc;
        ev.SearchText = Text.TurkishText.BuildSearchText(ev.Title, ev.DescriptionHtml, ev.LocationText);

        return ev;
    }

    /// <summary>
    /// Aynı UID'yi taşıyan kayıtları eşleştirir: RECURRENCE-ID taşıyanlar,
    /// taşımayan kökün istisnası olarak bağlanır.
    /// </summary>
    private static void LinkExceptionsToSeries(List<Event> parsed, List<string> warnings)
    {
        var rootsByUid = parsed
            .Where(e => e.RecurrenceId is null)
            .GroupBy(e => e.Uid, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var exception in parsed.Where(e => e.RecurrenceId is not null))
        {
            if (rootsByUid.TryGetValue(exception.Uid, out var root))
            {
                exception.SeriesId = root.Id;
            }
            else
            {
                // Kökü olmayan istisna tek başına anlamsızdır; bağımsız etkinliğe dönüştürülür.
                warnings.Add($"\"{exception.Title}\" için seri kökü bulunamadı, tekil etkinlik olarak alındı.");
                exception.RecurrenceId = null;
            }
        }
    }

    private static Availability ReadAvailability(CalendarEvent source)
    {
        var custom = source.Properties[AvailabilityProperty]?.Value?.ToString();
        if (custom is not null && Enum.TryParse<Availability>(custom, ignoreCase: true, out var parsed))
            return parsed;

        return string.Equals(source.Transparency, "TRANSPARENT", StringComparison.OrdinalIgnoreCase)
            ? Availability.Free
            : Availability.Busy;
    }

    // ------------------------------------------------------------------

    private static CalDateTime ToCalDateTime(LocalDateTime value, string? tzId, bool isAllDay)
        => isAllDay
            ? new CalDateTime(value.Date.ToDateOnly())
            : new CalDateTime(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second,
                              tzId ?? TimeZoneService.DefaultZoneId);

    private static CalDateTime ToUtcCalDateTime(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new CalDateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, "UTC");
    }

    private static LocalDateTime ToLocalDateTime(CalDateTime value)
        => value.HasTime
            ? new LocalDateTime(value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second)
            : new LocalDateTime(value.Year, value.Month, value.Day, 0, 0);

    /// <summary>
    /// ICS'teki zaman dilimi kimliğini IANA kimliğine indirger.
    /// Bazı istemciler "/mozilla.org/..." gibi ön ekler veya Windows adları yazar.
    /// </summary>
    private string ResolveZone(string? tzId, string fallback)
    {
        if (string.IsNullOrWhiteSpace(tzId)) return fallback;
        if (string.Equals(tzId, "UTC", StringComparison.OrdinalIgnoreCase)) return "UTC";
        if (timeZones.IsKnown(tzId)) return tzId;

        // "/freeassociation.sourceforge.net/Europe/Istanbul" gibi ön ekli kimlikler.
        var lastSlash = tzId.LastIndexOf('/');
        if (lastSlash > 0)
        {
            var candidate = tzId[(tzId.LastIndexOf('/', lastSlash - 1) + 1)..];
            if (timeZones.IsKnown(candidate)) return candidate;
        }

        // Windows kimliği olabilir; .NET'in dönüştürücüsü IANA karşılığını bilir.
        if (TimeZoneInfo.TryFindSystemTimeZoneById(tzId, out var windowsZone)
            && TimeZoneInfo.TryConvertWindowsIdToIanaId(windowsZone.Id, out var ianaId)
            && timeZones.IsKnown(ianaId))
        {
            return ianaId;
        }

        return fallback;
    }

    /// <summary>Dışa aktarılan dosya için önerilen ad.</summary>
    public static string SuggestFileName(string calendarName)
        => $"{Text.TurkishText.Normalize(calendarName).Replace(' ', '-')}-" +
           $"{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.ics";
}
