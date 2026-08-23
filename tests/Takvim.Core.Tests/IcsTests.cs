using Takvim.Core.Domain;
using Takvim.Core.Ics;
using static Takvim.Core.Tests.TestEvents;

namespace Takvim.Core.Tests;

public class IcsTests
{
    private readonly IcsSerializer _ics = new(Zones);
    private static readonly Guid TargetCalendar = Guid.NewGuid();

    /// <summary>Dışa aktarıp geri okur; kayıpsızlığı sınamanın en doğrudan yolu.</summary>
    private List<Event> RoundTrip(params Event[] events)
        => [.. _ics.Import(_ics.Export(events, "Kişisel"), TargetCalendar).Events];

    // ------------------------------------------------------------------
    // Temel alanlar
    // ------------------------------------------------------------------

    [Fact]
    public void Disa_aktarilan_dosya_gecerli_ics_basligi_tasir()
    {
        var ics = _ics.Export([Timed("2026-03-02 09:00", "2026-03-02 10:00")], "Kişisel");

        Assert.StartsWith("BEGIN:VCALENDAR", ics, StringComparison.Ordinal);
        Assert.Contains("VERSION:2.0", ics, StringComparison.Ordinal);
        Assert.Contains("BEGIN:VEVENT", ics, StringComparison.Ordinal);
        Assert.EndsWith("END:VCALENDAR\r\n", ics, StringComparison.Ordinal);
    }

    [Fact]
    public void Temel_alanlar_kayipsiz_gider_gelir()
    {
        var original = Timed("2026-03-02 09:00", "2026-03-02 10:30");
        original.Title = "Bütçe toplantısı";
        original.LocationText = "3. kat, Toplantı Odası";
        original.DescriptionHtml = "<p>Gündem: çeyreklik rakamlar</p>";

        var imported = RoundTrip(original).Single();

        Assert.Equal("Bütçe toplantısı", imported.Title);
        Assert.Equal("3. kat, Toplantı Odası", imported.LocationText);
        Assert.Contains("çeyreklik rakamlar", imported.DescriptionHtml, StringComparison.Ordinal);
        Assert.Equal(original.StartLocal, imported.StartLocal);
        Assert.Equal(original.EndLocal, imported.EndLocal);
        Assert.Equal(original.Uid, imported.Uid);
    }

    [Fact]
    public void Zaman_dilimi_korunur_ve_vtimezone_yazilir()
    {
        var ics = _ics.Export([Timed("2026-03-02 09:00", "2026-03-02 10:00", tzId: Istanbul)]);

        Assert.Contains("BEGIN:VTIMEZONE", ics, StringComparison.Ordinal);
        Assert.Contains("Europe/Istanbul", ics, StringComparison.Ordinal);

        var imported = _ics.Import(ics, TargetCalendar).Events.Single();
        Assert.Equal(Istanbul, imported.StartTimeZoneId);
        Assert.Equal(Parse("2026-03-02 09:00"), imported.StartLocal);
    }

    [Fact]
    public void Tum_gun_etkinligi_tarih_olarak_yazilir()
    {
        var ics = _ics.Export([AllDay("2026-06-01", days: 3)]);

        // Tüm gün etkinliği saat taşımaz: DTSTART;VALUE=DATE biçimindedir.
        Assert.Contains("DTSTART;VALUE=DATE:20260601", ics, StringComparison.Ordinal);
        Assert.Contains("DTEND;VALUE=DATE:20260604", ics, StringComparison.Ordinal);

        var imported = _ics.Import(ics, TargetCalendar).Events.Single();
        Assert.True(imported.IsAllDay);
        Assert.Null(imported.StartTimeZoneId);
    }

    // ------------------------------------------------------------------
    // Tekrarlama
    // ------------------------------------------------------------------

    [Fact]
    public void Tekrar_kurali_korunur()
    {
        var original = Timed("2026-03-02 09:00", "2026-03-02 10:00", "FREQ=WEEKLY;BYDAY=MO,WE");

        var imported = RoundTrip(original).Single();

        Assert.Contains("FREQ=WEEKLY", imported.RecurrenceRule, StringComparison.Ordinal);
        Assert.Contains("MO", imported.RecurrenceRule, StringComparison.Ordinal);
        Assert.Contains("WE", imported.RecurrenceRule, StringComparison.Ordinal);
    }

    [Fact]
    public void Cikarilan_tarihler_exdate_olarak_gider()
    {
        var original = Timed("2026-03-02 09:00", "2026-03-02 10:00", "FREQ=DAILY");
        original.ExDates = "2026-03-04T09:00:00";

        var ics = _ics.Export([original]);
        Assert.Contains("EXDATE", ics, StringComparison.Ordinal);

        var imported = _ics.Import(ics, TargetCalendar).Events.Single();
        Assert.Equal("2026-03-04T09:00:00", imported.ExDates);
    }

    [Fact]
    public void Istisna_satiri_recurrence_id_ile_gider_ve_seriye_baglanir()
    {
        var series = Timed("2026-03-02 09:00", "2026-03-02 10:00", "FREQ=DAILY");
        var exception = Exception(series, "2026-03-04 09:00", "2026-03-04 14:00", "2026-03-04 15:00");

        var ics = _ics.Export([series, exception]);
        Assert.Contains("RECURRENCE-ID", ics, StringComparison.Ordinal);

        var imported = _ics.Import(ics, TargetCalendar).Events;
        var root = imported.Single(e => e.RecurrenceId is null);
        var restored = imported.Single(e => e.RecurrenceId is not null);

        Assert.Equal(root.Uid, restored.Uid);
        Assert.Equal(root.Id, restored.SeriesId);
        Assert.Equal(Parse("2026-03-04 09:00"), restored.RecurrenceId);
        Assert.Equal(Parse("2026-03-04 14:00"), restored.StartLocal);
    }

    [Fact]
    public void Kokusuz_istisna_tekil_etkinlige_donusur()
    {
        var series = Timed("2026-03-02 09:00", "2026-03-02 10:00", "FREQ=DAILY");
        var exception = Exception(series, "2026-03-04 09:00", "2026-03-04 14:00", "2026-03-04 15:00");

        // Yalnızca istisnayı dışa aktar: karşı taraf kökü görmez.
        var result = _ics.Import(_ics.Export([exception]), TargetCalendar);

        Assert.Single(result.Events);
        Assert.Null(result.Events[0].RecurrenceId);
        Assert.Single(result.Warnings);
    }

    // ------------------------------------------------------------------
    // Durum ve görünürlük
    // ------------------------------------------------------------------

    [Fact]
    public void Serbest_etkinlik_transparent_olarak_gider()
    {
        var original = Timed("2026-03-02 09:00", "2026-03-02 10:00");
        original.Availability = Availability.Free;

        var ics = _ics.Export([original]);
        Assert.Contains("TRANSP:TRANSPARENT", ics, StringComparison.Ordinal);

        Assert.Equal(Availability.Free, _ics.Import(ics, TargetCalendar).Events.Single().Availability);
    }

    [Fact]
    public void Ofis_disi_durumu_ozel_alanla_korunur()
    {
        // Standart TRANSP yalnızca meşgul/serbest ayrımı yapar; ara durumlar kaybolur.
        var original = Timed("2026-03-02 09:00", "2026-03-02 10:00");
        original.Availability = Availability.OutOfOffice;

        var imported = RoundTrip(original).Single();

        Assert.Equal(Availability.OutOfOffice, imported.Availability);
    }

    [Fact]
    public void Ozel_etkinlik_class_private_olarak_gider()
    {
        var original = Timed("2026-03-02 09:00", "2026-03-02 10:00");
        original.Visibility = EventVisibility.Private;

        var ics = _ics.Export([original]);
        Assert.Contains("CLASS:PRIVATE", ics, StringComparison.Ordinal);

        Assert.Equal(EventVisibility.Private, _ics.Import(ics, TargetCalendar).Events.Single().Visibility);
    }

    // ------------------------------------------------------------------
    // Hatırlatıcılar
    // ------------------------------------------------------------------

    [Fact]
    public void Hatirlatici_valarm_olarak_gider()
    {
        var original = Timed("2026-03-02 09:00", "2026-03-02 10:00");
        original.Reminders.Add(new Reminder { EventId = original.Id, MinutesBefore = 15 });

        var ics = _ics.Export([original]);
        Assert.Contains("BEGIN:VALARM", ics, StringComparison.Ordinal);

        var imported = _ics.Import(ics, TargetCalendar).Events.Single();
        Assert.Equal(15, imported.Reminders.Single().MinutesBefore);
    }

    // ------------------------------------------------------------------
    // Bozuk ve yabancı girdiler
    // ------------------------------------------------------------------

    [Fact]
    public void Bozuk_dosya_cokme_yerine_uyari_dondurur()
    {
        var result = _ics.Import("bu bir ics dosyası değil", TargetCalendar);

        Assert.Empty(result.Events);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void Dtend_olmayan_kayit_rfc_varsayilanina_duser()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Test//TR
            BEGIN:VEVENT
            UID:tek@ornek
            DTSTART;VALUE=DATE:20260601
            SUMMARY:Bütün gün
            END:VEVENT
            END:VCALENDAR
            """;

        var imported = _ics.Import(ics, TargetCalendar).Events.Single();

        // DTEND yoksa tüm gün etkinliği bir gün sürer.
        Assert.True(imported.IsAllDay);
        Assert.Equal(Parse("2026-06-02 00:00"), imported.EndLocal);
    }

    [Fact]
    public void Windows_zaman_dilimi_kimligi_iana_karsiligina_cevrilir()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Outlook//TR
            BEGIN:VEVENT
            UID:windows@ornek
            DTSTART;TZID=Turkey Standard Time:20260302T090000
            DTEND;TZID=Turkey Standard Time:20260302T100000
            SUMMARY:Outlook'tan gelen
            END:VEVENT
            END:VCALENDAR
            """;

        var imported = _ics.Import(ics, TargetCalendar).Events.Single();

        Assert.Equal("Europe/Istanbul", imported.StartTimeZoneId);
        Assert.Equal(Parse("2026-03-02 09:00"), imported.StartLocal);
    }

    [Fact]
    public void Bilinmeyen_zaman_dilimi_varsayilana_duser()
    {
        const string ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Test//TR
            BEGIN:VEVENT
            UID:bilinmeyen@ornek
            DTSTART;TZID=Mars/Olympus:20260302T090000
            DTEND;TZID=Mars/Olympus:20260302T100000
            SUMMARY:Mars saati
            END:VEVENT
            END:VCALENDAR
            """;

        var imported = _ics.Import(ics, TargetCalendar, defaultZoneId: Istanbul).Events.Single();

        Assert.Equal(Istanbul, imported.StartTimeZoneId);
    }

    [Fact]
    public void Silinmis_etkinlik_disa_aktarilmaz()
    {
        var deleted = Timed("2026-03-02 09:00", "2026-03-02 10:00");
        deleted.DeletedAt = DateTimeOffset.UtcNow;

        Assert.DoesNotContain("BEGIN:VEVENT", _ics.Export([deleted]), StringComparison.Ordinal);
    }
}
