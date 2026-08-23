using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Scheduling;
using static Takvim.Core.Tests.TestEvents;

namespace Takvim.Core.Tests;

public class FreeBusyTests
{
    private static BusyInterval Busy(string start, string end, Availability kind = Availability.Busy)
        => new(Utc(start), Utc(end), kind);

    private static ScheduleLane Lane(string name, bool required = true, params BusyInterval[] busy)
        => new(Guid.NewGuid(), name, FreeBusy.Merge(busy), required);

    /// <summary>Öneri başlangıçlarını "09:00" biçiminde döker.</summary>
    private static string[] Times(IEnumerable<SlotSuggestion> slots)
        => [.. slots.Select(s => Zones.ToLocal(s.Start, Istanbul).ToString("HH:mm",
            System.Globalization.CultureInfo.InvariantCulture))];

    // ------------------------------------------------------------------
    // Aralık birleştirme
    // ------------------------------------------------------------------

    [Fact]
    public void Ustuste_binen_araliklar_birlestirilir()
    {
        var merged = FreeBusy.Merge([
            Busy("2026-03-02 09:00", "2026-03-02 10:00"),
            Busy("2026-03-02 09:30", "2026-03-02 11:00"),
        ]);

        Assert.Single(merged);
        Assert.Equal(Utc("2026-03-02 09:00"), merged[0].Start);
        Assert.Equal(Utc("2026-03-02 11:00"), merged[0].End);
    }

    [Fact]
    public void Bitisik_araliklar_birlestirilir()
    {
        var merged = FreeBusy.Merge([
            Busy("2026-03-02 09:00", "2026-03-02 10:00"),
            Busy("2026-03-02 10:00", "2026-03-02 11:00"),
        ]);

        Assert.Single(merged);
        Assert.Equal(Duration.FromHours(2), merged[0].Length);
    }

    [Fact]
    public void Ayrik_araliklar_birlestirilmez()
    {
        var merged = FreeBusy.Merge([
            Busy("2026-03-02 09:00", "2026-03-02 10:00"),
            Busy("2026-03-02 14:00", "2026-03-02 15:00"),
        ]);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Birlesmede_daha_engelleyici_tur_korunur()
    {
        // Belirsiz bir aralık meşgul bir aralıkla birleşince sonuç engelleyicidir.
        var merged = FreeBusy.Merge([
            Busy("2026-03-02 09:00", "2026-03-02 10:00", Availability.Tentative),
            Busy("2026-03-02 09:30", "2026-03-02 11:00", Availability.Busy),
        ]);

        Assert.Single(merged);
        Assert.True(merged[0].IsBlocking);
    }

    [Fact]
    public void Sifir_uzunluklu_aralik_atilir()
        => Assert.Empty(FreeBusy.Merge([Busy("2026-03-02 09:00", "2026-03-02 09:00")]));

    // ------------------------------------------------------------------
    // Meşguliyet türleri
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(Availability.Busy, true)]
    [InlineData(Availability.OutOfOffice, true)]
    [InlineData(Availability.FocusTime, true)]
    [InlineData(Availability.WorkingElsewhere, true)]
    [InlineData(Availability.Free, false)]
    [InlineData(Availability.Tentative, false)]
    public void Engelleyici_turler_dogru_ayrilir(Availability kind, bool blocking)
        => Assert.Equal(blocking, Busy("2026-03-02 09:00", "2026-03-02 10:00", kind).IsBlocking);

    // ------------------------------------------------------------------
    // Öneri üretimi
    // ------------------------------------------------------------------

    [Fact]
    public void Bos_takvimde_en_erken_aralik_onerilir()
    {
        var slots = FreeBusy.Suggest(
            [Lane("Ahmet")],
            Utc("2026-03-02 09:00"), Utc("2026-03-02 12:00"),
            Duration.FromHours(1), Duration.FromMinutes(30), maxResults: 3);

        Assert.Equal(["09:00", "09:30", "10:00"], Times(slots));
        Assert.All(slots, s => Assert.True(s.IsPerfect));
    }

    [Fact]
    public void Mesgul_aralik_atlanir()
    {
        var slots = FreeBusy.Suggest(
            [Lane("Ahmet", true, Busy("2026-03-02 09:00", "2026-03-02 10:30"))],
            Utc("2026-03-02 09:00"), Utc("2026-03-02 12:00"),
            Duration.FromHours(1), Duration.FromMinutes(30));

        // İlk çakışmasız aralık 10:30'dur.
        Assert.Equal("10:30", Times(slots)[0]);
        Assert.True(slots[0].IsPerfect);
    }

    [Fact]
    public void Iki_kisinin_ortak_bosu_bulunur()
    {
        var slots = FreeBusy.Suggest(
        [
            Lane("Ahmet", true, Busy("2026-03-02 09:00", "2026-03-02 10:00")),
            Lane("Ayşe", true, Busy("2026-03-02 10:00", "2026-03-02 11:00")),
        ],
            Utc("2026-03-02 09:00"), Utc("2026-03-02 13:00"),
            Duration.FromHours(1), Duration.FromMinutes(30));

        Assert.Equal("11:00", Times(slots)[0]);
    }

    [Fact]
    public void Hicbir_aralik_bos_degilse_en_az_cakisan_onerilir()
    {
        // Gerçek takvimlerde tümüyle boş aralık çoğu zaman yoktur; öneri
        // üretmemek yerine en az kötü seçenek sıralanır.
        var slots = FreeBusy.Suggest(
        [
            Lane("Ahmet", true, Busy("2026-03-02 09:00", "2026-03-02 12:00")),
            Lane("Ayşe", true, Busy("2026-03-02 09:00", "2026-03-02 10:00")),
        ],
            Utc("2026-03-02 09:00"), Utc("2026-03-02 12:00"),
            Duration.FromHours(1), Duration.FromMinutes(60));

        Assert.NotEmpty(slots);
        // En iyi seçenek yalnızca bir kişinin meşgul olduğu aralıktır.
        Assert.Equal(1, slots[0].ConflictingRequired);
        Assert.Equal("10:00", Times(slots)[0]);
    }

    [Fact]
    public void Zorunlu_katilimci_istege_bagliya_tercih_edilir()
    {
        var slots = FreeBusy.Suggest(
        [
            Lane("Zorunlu", true, Busy("2026-03-02 10:00", "2026-03-02 11:00")),
            Lane("İsteğe bağlı", false, Busy("2026-03-02 09:00", "2026-03-02 10:00")),
        ],
            Utc("2026-03-02 09:00"), Utc("2026-03-02 11:00"),
            Duration.FromHours(1), Duration.FromHours(1));

        // 09:00'da yalnızca isteğe bağlı meşgul, 10:00'da zorunlu meşgul.
        // İsteğe bağlının çakışması daha az cezalıdır.
        Assert.Equal("09:00", Times(slots)[0]);
    }

    [Fact]
    public void Belirsiz_etkinlik_ustune_toplanti_konabilir()
    {
        var slots = FreeBusy.Suggest(
            [Lane("Ahmet", true, Busy("2026-03-02 09:00", "2026-03-02 10:00", Availability.Tentative))],
            Utc("2026-03-02 09:00"), Utc("2026-03-02 11:00"),
            Duration.FromHours(1), Duration.FromHours(1));

        Assert.Equal("09:00", Times(slots)[0]);
        Assert.True(slots[0].IsPerfect);
    }

    [Fact]
    public void Mesai_disi_aralik_cezalandirilir()
    {
        var lane = new ScheduleLane(
            Guid.NewGuid(), "Ahmet", [], IsRequired: true,
            WorkingHours: (Utc("2026-03-02 09:00"), Utc("2026-03-02 18:00")));

        var slots = FreeBusy.Suggest(
            [lane],
            Utc("2026-03-02 07:00"), Utc("2026-03-02 11:00"),
            Duration.FromHours(1), Duration.FromHours(1));

        // Mesai içindeki 09:00 ve 10:00, mesai dışındaki 07:00 ve 08:00'den önce gelir.
        Assert.Equal(["09:00", "10:00"], Times(slots).Take(2));
        Assert.True(slots[0].IsPerfect);
    }

    [Fact]
    public void Pencereye_sigmayan_sure_oneri_uretmez()
        => Assert.Empty(FreeBusy.Suggest(
            [Lane("Ahmet")],
            Utc("2026-03-02 09:00"), Utc("2026-03-02 10:00"),
            Duration.FromHours(2), Duration.FromMinutes(30)));

    [Fact]
    public void Gecersiz_sure_oneri_uretmez()
    {
        Assert.Empty(FreeBusy.Suggest([Lane("A")], Utc("2026-03-02 09:00"), Utc("2026-03-02 17:00"),
            Duration.Zero, Duration.FromMinutes(30)));

        Assert.Empty(FreeBusy.Suggest([Lane("A")], Utc("2026-03-02 09:00"), Utc("2026-03-02 17:00"),
            Duration.FromHours(1), Duration.Zero));
    }

    // ------------------------------------------------------------------
    // Ortak boş aralıklar
    // ------------------------------------------------------------------

    [Fact]
    public void Ortak_bos_araliklar_bulunur()
    {
        var free = FreeBusy.CommonFreeIntervals(
        [
            Lane("Ahmet", true, Busy("2026-03-02 10:00", "2026-03-02 11:00")),
            Lane("Ayşe", true, Busy("2026-03-02 14:00", "2026-03-02 15:00")),
        ],
            Utc("2026-03-02 09:00"), Utc("2026-03-02 18:00"),
            Duration.FromMinutes(30));

        Assert.Equal(3, free.Count);
        Assert.Equal((Utc("2026-03-02 09:00"), Utc("2026-03-02 10:00")), free[0]);
        Assert.Equal((Utc("2026-03-02 11:00"), Utc("2026-03-02 14:00")), free[1]);
        Assert.Equal((Utc("2026-03-02 15:00"), Utc("2026-03-02 18:00")), free[2]);
    }

    [Fact]
    public void Asgari_uzunluktan_kisa_bosluklar_atlanir()
    {
        var free = FreeBusy.CommonFreeIntervals(
        [
            Lane("Ahmet", true,
                Busy("2026-03-02 09:00", "2026-03-02 10:00"),
                Busy("2026-03-02 10:15", "2026-03-02 12:00")),
        ],
            Utc("2026-03-02 09:00"), Utc("2026-03-02 12:00"),
            Duration.FromMinutes(30));

        // 10:00-10:15 arasındaki 15 dakikalık boşluk yeterli değil.
        Assert.Empty(free);
    }

    [Fact]
    public void Tumuyle_bos_pencere_tek_aralik_doner()
    {
        var free = FreeBusy.CommonFreeIntervals(
            [Lane("Ahmet")],
            Utc("2026-03-02 09:00"), Utc("2026-03-02 18:00"),
            Duration.FromMinutes(30));

        Assert.Single(free);
        Assert.Equal((Utc("2026-03-02 09:00"), Utc("2026-03-02 18:00")), free[0]);
    }
}
