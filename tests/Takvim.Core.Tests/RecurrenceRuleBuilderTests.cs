using NodaTime;
using Takvim.Core.Recurrence;

namespace Takvim.Core.Tests;

public class RecurrenceRuleBuilderTests
{
    private static readonly LocalDate Salı17Mart2026 = new(2026, 3, 17);

    // ------------------------------------------------------------------
    // Kural üretimi
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(RecurrencePreset.Daily, "FREQ=DAILY")]
    [InlineData(RecurrencePreset.WeeklyOnStartDay, "FREQ=WEEKLY;BYDAY=TU")]
    [InlineData(RecurrencePreset.EveryWeekday, "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR")]
    [InlineData(RecurrencePreset.MonthlyOnDayOfMonth, "FREQ=MONTHLY;BYMONTHDAY=17")]
    [InlineData(RecurrencePreset.MonthlyOnNthWeekday, "FREQ=MONTHLY;BYDAY=3TU")]
    [InlineData(RecurrencePreset.Yearly, "FREQ=YEARLY;BYMONTH=3;BYMONTHDAY=17")]
    public void Hazir_secenekler_dogru_kural_uretir(RecurrencePreset preset, string expected)
        => Assert.Equal(expected, RecurrenceRuleBuilder.FromPreset(preset, Salı17Mart2026));

    [Fact]
    public void Ayin_son_haftasindaki_gun_negatif_dizinle_ifade_edilir()
    {
        // 31 Mart 2026 salı, martın son salısıdır. "5. salı" her ayda olmadığı için
        // "-1TU" (son salı) kullanılmalıdır.
        var rule = RecurrenceRuleBuilder.FromPreset(RecurrencePreset.MonthlyOnNthWeekday, new LocalDate(2026, 3, 31));

        Assert.Equal("FREQ=MONTHLY;BYDAY=-1TU", rule);
    }

    // ------------------------------------------------------------------
    // UNTIL biçimi
    // ------------------------------------------------------------------

    [Fact]
    public void Until_gecerli_rfc5545_bicimiyle_yazilir()
    {
        // Bu test bir kez gerçekten kırıldı: CalDateTime.ToString çıktının sonuna
        // zaman dilimi adını ekleyince kural "...Z UTC" oluyor ve sessizce
        // çözümlenemiyordu; seri de süresiz sanılıyordu.
        var rule = RecurrenceRuleBuilder.TruncateBefore("FREQ=DAILY", Instant.FromUtc(2026, 3, 4, 6, 0));

        Assert.Equal("FREQ=DAILY;UNTIL=20260304T060000Z", rule);
    }

    [Fact]
    public void Kesme_islemi_count_yerine_until_kullanir()
    {
        // RFC 5545: COUNT ve UNTIL aynı kuralda bulunamaz.
        var rule = RecurrenceRuleBuilder.TruncateBefore("FREQ=DAILY;COUNT=10", Instant.FromUtc(2026, 3, 4, 6, 0));

        Assert.DoesNotContain("COUNT", rule, StringComparison.Ordinal);
        Assert.Contains("UNTIL=", rule, StringComparison.Ordinal);
    }

    [Fact]
    public void Until_dahil_edicidir()
    {
        // 06:00 UTC = 09:00 İstanbul. RFC 5545'e göre UNTIL anındaki örnek hâlâ üretilir.
        var rule = RecurrenceRuleBuilder.TruncateBefore("FREQ=DAILY", Instant.FromUtc(2026, 3, 4, 6, 0));

        Assert.Equal(
            ["2026-03-02 09:00", "2026-03-03 09:00", "2026-03-04 09:00"],
            ExpandMart(rule));
    }

    [Fact]
    public void Bir_saniye_geriden_kesmek_o_ornegi_disarida_birakir()
    {
        // Seri bölme işleminin kullandığı yol budur: bölünme noktasındaki örnek
        // eski seride kalmamalı, yeni seride yer almalıdır.
        var rule = RecurrenceRuleBuilder.TruncateBefore(
            "FREQ=DAILY", Instant.FromUtc(2026, 3, 4, 6, 0) - Duration.FromSeconds(1));

        Assert.Equal(["2026-03-02 09:00", "2026-03-03 09:00"], ExpandMart(rule));
    }

    private static string[] ExpandMart(string rule)
    {
        var ev = TestEvents.Timed("2026-03-02 09:00", "2026-03-02 10:00", rule);
        var expander = new RecurrenceExpander(TestEvents.Zones);

        return expander
            .Expand(ev, null, TestEvents.Utc("2026-03-01 00:00"), TestEvents.Utc("2026-04-01 00:00"))
            .ToList().Starts();
    }

    // ------------------------------------------------------------------
    // Doğrulama
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("FREQ=DAILY")]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE")]
    [InlineData("FREQ=MONTHLY;BYDAY=3TU;COUNT=12")]
    public void Gecerli_kurallar_kabul_edilir(string? rule)
    {
        Assert.True(RecurrenceRuleBuilder.TryParse(rule, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("FREQ=SECONDLY")]
    [InlineData("FREQ=MINUTELY")]
    [InlineData("FREQ=DAILY;COUNT=0")]
    public void Desteklenmeyen_kurallar_reddedilir(string rule)
    {
        Assert.False(RecurrenceRuleBuilder.TryParse(rule, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    // ------------------------------------------------------------------
    // Türkçe açıklama
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(null, "Tekrarlamaz")]
    [InlineData("FREQ=DAILY", "Her gün")]
    [InlineData("FREQ=DAILY;INTERVAL=2", "İki günde bir")]
    [InlineData("FREQ=DAILY;INTERVAL=5", "5 günde bir")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO", "Her hafta pazartesi")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,WE,FR", "Her hafta pazartesi, çarşamba ve cuma")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR", "Hafta içi her gün")]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE", "İki haftada bir pazartesi ve çarşamba")]
    [InlineData("FREQ=MONTHLY;BYDAY=3TU", "Her ay üçüncü salı")]
    [InlineData("FREQ=MONTHLY;BYDAY=-1FR", "Her ay son cuma")]
    [InlineData("FREQ=MONTHLY;BYMONTHDAY=15", "Her ay ayın 15'inde")]
    [InlineData("FREQ=YEARLY;BYMONTH=1;BYMONTHDAY=1", "Her yıl 1 Ocak")]
    public void Kural_turkce_okunur_hale_getirilir(string? rule, string expected)
        => Assert.Equal(expected, RecurrenceRuleBuilder.Describe(rule, Salı17Mart2026));

    [Fact]
    public void Bitis_kosulu_aciklamaya_eklenir()
    {
        Assert.Equal("Her gün, 10 kez",
            RecurrenceRuleBuilder.Describe("FREQ=DAILY;COUNT=10", Salı17Mart2026));

        Assert.Equal("Her gün, 31.12.2026 tarihine kadar",
            RecurrenceRuleBuilder.Describe("FREQ=DAILY;UNTIL=20261231T000000Z", Salı17Mart2026));
    }
}
