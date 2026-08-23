using Takvim.Core.Domain;
using Takvim.Core.Permissions;

namespace Takvim.Core.Tests;

/// <summary>
/// İzin motorunun docs/izin-modeli.md dosyasındaki tabloya birebir uyduğunu
/// doğrular. Tablo değişirse önce bu testler kırılmalıdır.
/// </summary>
public class PermissionTests
{
    private static readonly Guid Viewer = Guid.NewGuid();

    private static ViewerContext SharedAt(SharingLevel level) => new(Viewer, SharingLevel: level);

    private static EventFacts Event(
        EventVisibility visibility = EventVisibility.Default,
        bool privateCategory = false,
        Availability availability = Availability.Busy,
        bool deleted = false)
        => new(visibility, privateCategory, availability, deleted);

    // ==================================================================
    // Sahiplik
    // ==================================================================

    [Fact]
    public void Sahip_her_seyi_gorur_ve_duzenler()
    {
        var access = CalendarAccess.Resolve(new ViewerContext(Viewer, IsOwner: true), Event());

        Assert.Equal(DetailLevel.FullDetails, access.Detail);
        Assert.True(access.CanEdit);
        Assert.True(access.CanInviteOnBehalf);
    }

    [Fact]
    public void Sahip_kendi_ozel_etkinligini_gorur()
    {
        var access = CalendarAccess.Resolve(
            new ViewerContext(Viewer, IsOwner: true),
            Event(EventVisibility.Private, privateCategory: true));

        Assert.Equal(DetailLevel.FullDetails, access.Detail);
    }

    // ==================================================================
    // Paylaşım seviyeleri — kesişim tablosunun ilk satırı
    // ==================================================================

    [Theory]
    [InlineData(SharingLevel.None, DetailLevel.None, false, false)]
    [InlineData(SharingLevel.FreeBusy, DetailLevel.BusyOnly, false, false)]
    [InlineData(SharingLevel.TitleLocation, DetailLevel.TitleLocation, false, false)]
    [InlineData(SharingLevel.FullDetails, DetailLevel.FullDetails, false, false)]
    [InlineData(SharingLevel.CanEdit, DetailLevel.FullDetails, true, false)]
    [InlineData(SharingLevel.FullControl, DetailLevel.FullDetails, true, true)]
    public void Varsayilan_gorunurlukte_taban_paylasim_seviyesidir(
        SharingLevel level, DetailLevel expected, bool canEdit, bool canInvite)
    {
        var access = CalendarAccess.Resolve(SharedAt(level), Event());

        Assert.Equal(expected, access.Detail);
        Assert.Equal(canEdit, access.CanEdit);
        Assert.Equal(canInvite, access.CanInviteOnBehalf);
    }

    // ==================================================================
    // Etkinlik görünürlüğü
    // ==================================================================

    [Theory]
    [InlineData(SharingLevel.FreeBusy)]
    [InlineData(SharingLevel.TitleLocation)]
    [InlineData(SharingLevel.FullDetails)]
    public void Herkese_acik_etkinlik_tabani_yukseltir(SharingLevel level)
    {
        // Takvim yalnızca meşgul/müsait paylaşılsa bile bu etkinliğin detayı görünür.
        var access = CalendarAccess.Resolve(SharedAt(level), Event(EventVisibility.Public));

        Assert.Equal(DetailLevel.FullDetails, access.Detail);
    }

    [Theory]
    [InlineData(SharingLevel.FreeBusy)]
    [InlineData(SharingLevel.TitleLocation)]
    [InlineData(SharingLevel.FullDetails)]
    [InlineData(SharingLevel.CanEdit)]
    [InlineData(SharingLevel.FullControl)]
    public void Ozel_etkinlik_her_seviyede_yalnizca_mesgul_gosterir(SharingLevel level)
    {
        var access = CalendarAccess.Resolve(SharedAt(level), Event(EventVisibility.Private));

        Assert.Equal(DetailLevel.BusyOnly, access.Detail);
        Assert.False(access.CanEdit);
        Assert.False(access.CanInviteOnBehalf);
    }

    // ==================================================================
    // Kategori gizliliği
    // ==================================================================

    [Theory]
    [InlineData(SharingLevel.FreeBusy)]
    [InlineData(SharingLevel.FullDetails)]
    [InlineData(SharingLevel.FullControl)]
    public void Gizli_kategori_her_seviyede_kisitlar(SharingLevel level)
    {
        var access = CalendarAccess.Resolve(SharedAt(level), Event(privateCategory: true));

        Assert.Equal(DetailLevel.BusyOnly, access.Detail);
        Assert.False(access.CanEdit);
    }

    [Fact]
    public void Gizli_kategori_herkese_acik_isaretini_yener()
    {
        // Belgede karara bağlanan çakışma kuralı: daraltma genişletmeyi yener.
        var access = CalendarAccess.Resolve(
            SharedAt(SharingLevel.FullDetails),
            Event(EventVisibility.Public, privateCategory: true));

        Assert.Equal(DetailLevel.BusyOnly, access.Detail);
    }

    // ==================================================================
    // Müsait + özel: hiç gösterilmez
    // ==================================================================

    [Fact]
    public void Musait_ozel_etkinlik_hic_gosterilmez()
    {
        // Ne bilgi taşır ne yer kaplar; ızgarada çizilmesi anlamsızdır.
        var access = CalendarAccess.Resolve(
            SharedAt(SharingLevel.FullDetails),
            Event(EventVisibility.Private, availability: Availability.Free));

        Assert.True(access.IsHidden);
    }

    [Fact]
    public void Musait_ama_ozel_olmayan_etkinlik_gosterilir()
    {
        var access = CalendarAccess.Resolve(
            SharedAt(SharingLevel.FullDetails),
            Event(availability: Availability.Free));

        Assert.Equal(DetailLevel.FullDetails, access.Detail);
    }

    // ==================================================================
    // Vekil erişimi
    // ==================================================================

    [Fact]
    public void Vekil_normal_etkinlikte_devraldigi_yetkiyi_kullanir()
    {
        var viewer = new ViewerContext(Viewer, SharingLevel: SharingLevel.CanEdit, IsDelegate: true);
        var access = CalendarAccess.Resolve(viewer, Event());

        Assert.Equal(DetailLevel.FullDetails, access.Detail);
        Assert.True(access.CanEdit);
    }

    [Fact]
    public void Ozel_ogeleri_goremeyen_vekil_ozel_etkinligi_goremez()
    {
        var viewer = new ViewerContext(Viewer, SharingLevel: SharingLevel.CanEdit, IsDelegate: true);
        var access = CalendarAccess.Resolve(viewer, Event(EventVisibility.Private));

        Assert.Equal(DetailLevel.BusyOnly, access.Detail);
        Assert.False(access.CanEdit);
    }

    [Fact]
    public void Ozel_ogeleri_gorebilen_vekil_ozel_etkinligi_gorur()
    {
        var viewer = new ViewerContext(Viewer,
            SharingLevel: SharingLevel.CanEdit, IsDelegate: true, DelegateCanSeePrivateItems: true);

        var access = CalendarAccess.Resolve(viewer, Event(EventVisibility.Private));

        Assert.Equal(DetailLevel.FullDetails, access.Detail);
        Assert.True(access.CanEdit);
    }

    [Fact]
    public void Ozel_ogeleri_gorebilen_vekil_gizli_kategoriyi_de_gorur()
    {
        var viewer = new ViewerContext(Viewer,
            SharingLevel: SharingLevel.FullDetails, IsDelegate: true, DelegateCanSeePrivateItems: true);

        var access = CalendarAccess.Resolve(viewer, Event(privateCategory: true));

        Assert.Equal(DetailLevel.FullDetails, access.Detail);
    }

    // ==================================================================
    // Katılımcılık
    // ==================================================================

    [Fact]
    public void Katilimci_takvim_hic_paylasilmamis_olsa_bile_tum_detayi_gorur()
    {
        var viewer = new ViewerContext(Viewer, SharingLevel: SharingLevel.None, IsAttendee: true);
        var access = CalendarAccess.Resolve(viewer, Event(EventVisibility.Private, privateCategory: true));

        Assert.Equal(DetailLevel.FullDetails, access.Detail);
        // Ancak davetli olmak düzenleme yetkisi vermez.
        Assert.False(access.CanEdit);
    }

    // ==================================================================
    // Çöp kutusu
    // ==================================================================

    [Fact]
    public void Silinmis_etkinlik_paylasimlarda_gorunmez()
    {
        Assert.True(CalendarAccess.Resolve(SharedAt(SharingLevel.FullControl), Event(deleted: true)).IsHidden);
        Assert.True(CalendarAccess.Resolve(
            new ViewerContext(Viewer, IsAttendee: true), Event(deleted: true)).IsHidden);
    }

    [Fact]
    public void Silinmis_etkinligi_duzenleme_yetkili_vekil_gorur()
    {
        var viewer = new ViewerContext(Viewer, SharingLevel: SharingLevel.CanEdit, IsDelegate: true);

        Assert.False(CalendarAccess.Resolve(viewer, Event(deleted: true)).IsHidden);
    }

    [Fact]
    public void Sahip_silinmis_etkinligi_gorur()
    {
        var access = CalendarAccess.Resolve(new ViewerContext(Viewer, IsOwner: true), Event(deleted: true));

        Assert.Equal(DetailLevel.FullDetails, access.Detail);
    }

    // ==================================================================
    // Gösterilecek metin
    // ==================================================================

    [Fact]
    public void Kisitli_etkinligin_basligi_mesgul_yazar()
    {
        var access = CalendarAccess.Resolve(SharedAt(SharingLevel.FullDetails), Event(EventVisibility.Private));

        Assert.Equal("Meşgul", CalendarAccess.TitleFor(access, "Doktor randevusu"));
        Assert.Null(CalendarAccess.LocationFor(access, "Hastane"));
    }

    [Fact]
    public void Baslik_ve_konum_seviyesinde_aciklama_gorunmez()
    {
        var access = CalendarAccess.Resolve(SharedAt(SharingLevel.TitleLocation), Event());

        Assert.Equal("Bütçe toplantısı", CalendarAccess.TitleFor(access, "Bütçe toplantısı"));
        Assert.Equal("3. kat", CalendarAccess.LocationFor(access, "3. kat"));
        Assert.False(access.CanSeeDetails);
    }

    // ==================================================================
    // Tablo bütünlüğü
    // ==================================================================

    [Fact]
    public void Hicbir_kombinasyon_beklenmeyen_yetki_uretmez()
    {
        // Kaba kuvvetle tüm kombinasyonlar taranır; iki değişmez sınanır:
        // (1) kısıtlı bir etkinlikte düzenleme yetkisi verilemez,
        // (2) paylaşılmamış bir takvimde, davetli değilken hiçbir şey görünmez.
        var levels = Enum.GetValues<SharingLevel>();
        var visibilities = Enum.GetValues<EventVisibility>();
        var availabilities = Enum.GetValues<Availability>();

        foreach (var level in levels)
        foreach (var visibility in visibilities)
        foreach (var availability in availabilities)
        foreach (var privateCategory in new[] { false, true })
        foreach (var isDelegate in new[] { false, true })
        foreach (var seesPrivate in new[] { false, true })
        {
            var viewer = new ViewerContext(Viewer,
                SharingLevel: level, IsDelegate: isDelegate, DelegateCanSeePrivateItems: seesPrivate);

            var access = CalendarAccess.Resolve(viewer, new EventFacts(visibility, privateCategory, availability));

            if (access.Detail <= DetailLevel.BusyOnly)
            {
                Assert.False(access.CanEdit, $"{level}/{visibility}/gizli={privateCategory} düzenleme vermemeli");
                Assert.False(access.CanInviteOnBehalf);
            }

            if (level == SharingLevel.None)
            {
                Assert.True(access.IsHidden, "Paylaşılmamış takvimde hiçbir şey görünmemeli");
            }
        }
    }
}
