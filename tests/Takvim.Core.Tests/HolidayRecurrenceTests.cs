using Takvim.Core.Domain;
using Takvim.Core.Localization;
using Takvim.Core.Recurrence;
using static Takvim.Core.Tests.TestEvents;

namespace Takvim.Core.Tests;

/// <summary>
/// Resmi tatile denk gelen tekrarların atlanması ve ertelenmesi.
/// <para>
/// RRULE'un kendisi tatilden habersizdir; tatil takvimi ayrı bir kaynaktır ve
/// kural örnek üretildikten sonra uygulanır.
/// </para>
/// </summary>
public class HolidayRecurrenceTests
{
    private readonly RecurrenceExpander _expander = new(Zones, new TurkishHolidays());

    private string[] Expand(Event root, string from, string to)
        => _expander.Expand(root, null, Utc(from), Utc(to))
            .OrderBy(o => o.StartUtc)
            .ToArray()
            .Starts();

    /// <summary>
    /// 20-22 Mart 2026 Ramazan Bayramı; 20 Mart cuma. Haftaiçi her gün tekrar
    /// eden bir etkinlik bu üç güne denk gelir.
    /// </summary>
    private static Event WeekdayMeeting(HolidayBehavior behavior)
    {
        var ev = Timed("2026-03-16 09:00", "2026-03-16 10:00", "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR");
        ev.HolidayBehavior = behavior;
        return ev;
    }

    // ==================================================================
    // Varsayılan: tatilde de üretilir
    // ==================================================================

    [Fact]
    public void Varsayilan_kural_tatilde_de_uretir()
    {
        var result = Expand(WeekdayMeeting(HolidayBehavior.Include), "2026-03-16 00:00", "2026-03-21 00:00");

        // Pazartesiden cumaya beş örnek; 20 Mart tatil olmasına rağmen var.
        Assert.Equal(
            ["2026-03-16 09:00", "2026-03-17 09:00", "2026-03-18 09:00",
             "2026-03-19 09:00", "2026-03-20 09:00"],
            result);
    }

    // ==================================================================
    // Atlama
    // ==================================================================

    [Fact]
    public void Atlama_tatile_denk_gelen_ornegi_uretmez()
    {
        var result = Expand(WeekdayMeeting(HolidayBehavior.Skip), "2026-03-16 00:00", "2026-03-21 00:00");

        // 20 Mart (Ramazan Bayramı 1. gün) düşer.
        Assert.Equal(
            ["2026-03-16 09:00", "2026-03-17 09:00", "2026-03-18 09:00", "2026-03-19 09:00"],
            result);
    }

    [Fact]
    public void Atlama_ardisik_tatil_gunlerinin_hepsini_atlar()
    {
        // 20, 21, 22 Mart bayram; 23 Mart pazartesi normal.
        var result = Expand(WeekdayMeeting(HolidayBehavior.Skip), "2026-03-20 00:00", "2026-03-24 00:00");

        Assert.Equal(["2026-03-23 09:00"], result);
    }

    [Fact]
    public void Atlama_yarim_gun_arefeyi_atlamaz()
    {
        // 19 Mart arefe: yarım gün mesai, tam gün tatil değil.
        var result = Expand(WeekdayMeeting(HolidayBehavior.Skip), "2026-03-19 00:00", "2026-03-20 00:00");

        Assert.Equal(["2026-03-19 09:00"], result);
    }

    // ==================================================================
    // Erteleme
    // ==================================================================

    [Fact]
    public void Erteleme_tatilden_sonraki_ilk_is_gunune_tasir()
    {
        // 20 Mart cuma tatil; 21-22 bayram, 21-22 aynı zamanda hafta sonu değil
        // ama tatil. Sonraki iş günü 23 Mart pazartesi.
        var result = Expand(
            WeekdayMeeting(HolidayBehavior.MoveToNextWorkingDay), "2026-03-20 00:00", "2026-03-24 00:00");

        // 20 Mart örneği 23 Mart'a taşındı; 23 Mart'ın kendi örneği de var.
        Assert.Equal(["2026-03-23 09:00", "2026-03-23 09:00"], result);
    }

    [Fact]
    public void Erteleme_saati_korur()
    {
        var ev = Timed("2026-04-20 14:30", "2026-04-20 15:30", "FREQ=DAILY");
        ev.HolidayBehavior = HolidayBehavior.MoveToNextWorkingDay;

        // 23 Nisan perşembe tatil; 24 Nisan cuma iş günü.
        var occurrence = _expander
            .Expand(ev, null, Utc("2026-04-23 00:00"), Utc("2026-04-24 00:00"))
            .ToList();

        Assert.Empty(occurrence);

        var moved = _expander
            .Expand(ev, null, Utc("2026-04-24 00:00"), Utc("2026-04-25 00:00"))
            .OrderBy(o => o.StartUtc)
            .ToList();

        // 23 Nisan örneği 24 Nisan'a taşındı, saati 14:30 kaldı.
        Assert.Contains(moved, o => o.StartLocal == Parse("2026-04-24 14:30"));
    }

    [Fact]
    public void Erteleme_hafta_sonunu_atlar()
    {
        // 1 Mayıs 2026 cuma: Emek ve Dayanışma Günü. Sonraki iş günü 4 Mayıs pazartesi.
        var ev = Timed("2026-05-01 09:00", "2026-05-01 10:00", "FREQ=YEARLY");
        ev.HolidayBehavior = HolidayBehavior.MoveToNextWorkingDay;

        var result = Expand(ev, "2026-05-01 00:00", "2026-05-06 00:00");

        Assert.Equal(["2026-05-04 09:00"], result);
    }

    [Fact]
    public void Ertelenen_ornek_ozgun_kimligini_korur()
    {
        // "Bu etkinliği düzenle" doğru örneği bulabilmeli; kimlik özgün saattir.
        var ev = Timed("2026-04-23 09:00", "2026-04-23 10:00", "FREQ=YEARLY");
        ev.HolidayBehavior = HolidayBehavior.MoveToNextWorkingDay;

        var occurrence = _expander
            .Expand(ev, null, Utc("2026-04-24 00:00"), Utc("2026-04-25 00:00"))
            .Single();

        Assert.Equal(Parse("2026-04-24 09:00"), occurrence.StartLocal);
        Assert.Equal(Parse("2026-04-23 09:00"), occurrence.RecurrenceId);
    }

    // ==================================================================
    // Tatil takvimi verilmezse
    // ==================================================================

    [Fact]
    public void Tatil_takvimi_yoksa_kural_uygulanmaz()
    {
        // Tatil bilgisi olmadan "atla" isteğine uyulamaz; örnek üretilir.
        var expander = new RecurrenceExpander(Zones);
        var ev = WeekdayMeeting(HolidayBehavior.Skip);

        var result = expander
            .Expand(ev, null, Utc("2026-03-20 00:00"), Utc("2026-03-21 00:00"))
            .ToArray()
            .Starts();

        Assert.Equal(["2026-03-20 09:00"], result);
    }

    [Fact]
    public void Tekrarlamayan_etkinlik_kuraldan_etkilenmez()
    {
        var ev = Timed("2026-03-20 09:00", "2026-03-20 10:00");
        ev.HolidayBehavior = HolidayBehavior.Skip;

        Assert.Equal(["2026-03-20 09:00"], Expand(ev, "2026-03-20 00:00", "2026-03-21 00:00"));
    }
}
