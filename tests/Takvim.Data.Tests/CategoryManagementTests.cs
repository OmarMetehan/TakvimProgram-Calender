using Microsoft.EntityFrameworkCore;
using Takvim.Core.Domain;
using Takvim.Core.Permissions;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// Kategori yönetimi. Takvimlerden iki noktada ayrılır ve ikisi de ayrıca
/// sınanır: kategori silinince etkinlikler silinmez (yalnızca etiket düşer),
/// ve "gizli" işareti izin motorunun dördüncü boyutudur — değiştirildiğinde
/// görünürlük anında değişmelidir.
/// </summary>
public class CategoryManagementTests : IDisposable
{
    private readonly TestDatabase _t = new();
    private readonly CategoryService _categories;

    public CategoryManagementTests() => _categories = new CategoryService(_t.Db, _t.Clock);

    public void Dispose()
    {
        _t.Dispose();
        GC.SuppressFinalize(this);
    }

    private Task<Category> CreateAsync(
        string name = "Proje", string color = "basil", bool isPrivate = false)
        => _categories.CreateAsync(
            _t.UserId, new CategoryInput { Name = name, Color = color, IsPrivate = isPrivate });

    /// <summary>Verilen kategorileri taşıyan bir etkinlik açar.</summary>
    private async Task<Guid> CreateEventAsync(params Guid[] categoryIds)
    {
        var input = _t.Input("2026-03-02 10:00", "2026-03-02 11:00") with
        {
            CategoryIds = categoryIds,
        };

        var created = await _t.Events.CreateAsync(input);
        _t.Detach();

        return created.PrimaryEventId;
    }

    // ==================================================================
    // Oluşturma
    // ==================================================================

    [Fact]
    public async Task Kategori_olusturulur()
    {
        var category = await CreateAsync();
        _t.Detach();

        Assert.Equal("Proje", category.Name);
        Assert.Equal("basil", category.Color);
        Assert.False(category.IsPrivate);
    }

    [Fact]
    public async Task Addaki_bosluklar_kirpilir()
        => Assert.Equal("Proje", (await CreateAsync("  Proje  ")).Name);

    [Fact]
    public async Task Bos_ad_reddedilir()
        => await Assert.ThrowsAsync<ArgumentException>(() => CreateAsync("   "));

    [Fact]
    public async Task Ayni_ad_ikinci_kez_acilamaz()
    {
        // Süzgeçte hangisinin hangisi olduğu anlaşılmazdı.
        await CreateAsync("Proje");
        _t.Detach();

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateAsync("Proje"));
    }

    [Fact]
    public async Task Baska_kullanicida_ayni_ad_serbest()
    {
        await CreateAsync("Proje");
        _t.Detach();

        var (otherUserId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");

        var mine = await _categories.CreateAsync(
            otherUserId, new CategoryInput { Name = "Proje" });

        Assert.Equal("Proje", mine.Name);
    }

    [Fact]
    public async Task Kategori_sayisi_sinirlanir()
    {
        var existing = (await _categories.GetOwnAsync(_t.UserId)).Count;

        for (var i = existing; i < CategoryService.MaxPerUser; i++)
        {
            await CreateAsync($"Kategori {i}");
            _t.Detach();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateAsync("Fazlalık"));
    }

    // ==================================================================
    // Güncelleme
    // ==================================================================

    [Fact]
    public async Task Kategori_guncellenir()
    {
        var category = await CreateAsync();
        _t.Detach();

        var problem = await _categories.UpdateAsync(category.Id, new CategoryInput
        {
            Name = "Yeni ad",
            Color = "grape",
            IsPrivate = true,
        });

        _t.Detach();

        Assert.Null(problem);

        var reloaded = await _t.Db.Categories.AsNoTracking().FirstAsync(c => c.Id == category.Id);

        Assert.Equal("Yeni ad", reloaded.Name);
        Assert.Equal("grape", reloaded.Color);
        Assert.True(reloaded.IsPrivate);
    }

    [Fact]
    public async Task Var_olan_bir_ada_cevrilemez()
    {
        await CreateAsync("Birinci");
        _t.Detach();
        var second = await CreateAsync("İkinci");
        _t.Detach();

        Assert.NotNull(await _categories.UpdateAsync(
            second.Id, new CategoryInput { Name = "Birinci" }));
    }

    [Fact]
    public async Task Kendi_adini_korumak_serbest()
    {
        var category = await CreateAsync("Proje");
        _t.Detach();

        // Yalnızca rengi değiştirmek, ad çakışması sayılmamalı.
        Assert.Null(await _categories.UpdateAsync(
            category.Id, new CategoryInput { Name = "Proje", Color = "tomato" }));
    }

    [Fact]
    public async Task Olmayan_kategori_guncellenemez()
        => Assert.NotNull(await _categories.UpdateAsync(
            Guid.NewGuid(), new CategoryInput { Name = "x" }));

    [Fact]
    public async Task Kategoriler_yeniden_siralanir()
    {
        var first = await CreateAsync("Bir");
        _t.Detach();
        var second = await CreateAsync("İki");
        _t.Detach();

        await _categories.ReorderAsync(_t.UserId, [second.Id, first.Id]);
        _t.Detach();

        var order = await _categories.GetOwnAsync(_t.UserId);
        var positions = order.Select(c => c.Name).ToList();

        Assert.True(positions.IndexOf("İki") < positions.IndexOf("Bir"));
    }

    // ==================================================================
    // Silme
    // ==================================================================

    [Fact]
    public async Task Silinen_kategori_listeden_cikar()
    {
        var category = await CreateAsync();
        _t.Detach();

        await _categories.DeleteAsync(category.Id);
        _t.Detach();

        Assert.DoesNotContain(await _categories.GetOwnAsync(_t.UserId), c => c.Id == category.Id);
    }

    [Fact]
    public async Task Kategori_silinince_etkinlik_silinmez()
    {
        var category = await CreateAsync();
        _t.Detach();

        var eventId = await CreateEventAsync(category.Id);

        await _categories.DeleteAsync(category.Id);
        _t.Detach();

        // Kategori bir sınıflandırmadır, içinde bir şey barındırmaz.
        var ev = await _t.Db.Events.AsNoTracking().FirstAsync(e => e.Id == eventId);

        Assert.Null(ev.DeletedAt);
        Assert.Empty(await _t.Db.EventCategories.Where(ec => ec.EventId == eventId).ToListAsync());
    }

    [Fact]
    public async Task Diger_etiketler_kalir()
    {
        var kept = await CreateAsync("Kalan");
        _t.Detach();
        var removed = await CreateAsync("Silinen");
        _t.Detach();

        var eventId = await CreateEventAsync(kept.Id, removed.Id);

        await _categories.DeleteAsync(removed.Id);
        _t.Detach();

        var remaining = await _t.Db.EventCategories
            .Where(ec => ec.EventId == eventId)
            .Select(ec => ec.CategoryId)
            .ToListAsync();

        Assert.Equal([kept.Id], remaining);
    }

    // ==================================================================
    // Kullanım özeti
    // ==================================================================

    [Fact]
    public async Task Kullanim_sayisi_dogru()
    {
        var category = await CreateAsync();
        _t.Detach();

        await CreateEventAsync(category.Id);
        await CreateEventAsync(category.Id);

        Assert.Equal(2, await _categories.GetUsageAsync(category.Id));
    }

    [Fact]
    public async Task Silinen_etkinlik_kullanimda_sayilmaz()
    {
        var category = await CreateAsync();
        _t.Detach();

        var eventId = await CreateEventAsync(category.Id);

        await _t.Events.DeleteAsync(eventId, null, SeriesEditScope.AllInSeries, _t.UserId);
        _t.Detach();

        Assert.Equal(0, await _categories.GetUsageAsync(category.Id));
    }

    // ==================================================================
    // Gizlilik: izin motorunun dördüncü boyutu
    // ==================================================================

    [Fact]
    public async Task Gizli_kategori_paylasimda_etkinligi_karartir()
    {
        var category = await CreateAsync("Gizli", isPrivate: true);
        _t.Detach();

        await CreateEventAsync(category.Id);

        var (viewerUserId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");
        _t.Share(_t.CalendarId, viewerUserId, SharingLevel.FullDetails);

        var seen = Assert.Single(await _t.SeenByAsync(viewerUserId, "2026-03-02 00:00", "2026-03-03 00:00"));

        // Tüm detay paylaşılmış olsa bile gizli kategori başlığı düşürür.
        Assert.NotEqual("Toplantı", seen.Source.Title);
        Assert.False(seen.CanSeeDetails);
    }

    [Fact]
    public async Task Gizlilik_kaldirilinca_etkinlik_gorunur_olur()
    {
        var category = await CreateAsync("Gizli", isPrivate: true);
        _t.Detach();

        await CreateEventAsync(category.Id);

        var (viewerUserId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");
        _t.Share(_t.CalendarId, viewerUserId, SharingLevel.FullDetails);

        await _categories.UpdateAsync(
            category.Id, new CategoryInput { Name = "Gizli", IsPrivate = false });

        _t.Detach();

        var seen = Assert.Single(await _t.SeenByAsync(viewerUserId, "2026-03-02 00:00", "2026-03-03 00:00"));

        Assert.Equal("Toplantı", seen.Source.Title);
        Assert.True(seen.CanSeeDetails);
    }

    [Fact]
    public async Task Sahibi_gizli_kategorisini_her_zaman_gorur()
    {
        var category = await CreateAsync("Gizli", isPrivate: true);
        _t.Detach();

        await CreateEventAsync(category.Id);

        var seen = Assert.Single(await _t.SeenByAsync(_t.UserId, "2026-03-02 00:00", "2026-03-03 00:00"));

        Assert.Equal("Toplantı", seen.Source.Title);
    }

    // ==================================================================
    // Görünürlük
    // ==================================================================

    [Fact]
    public async Task Baska_kullanicinin_kategorisi_listede_gorunmez()
    {
        await CreateAsync("Benim");
        _t.Detach();

        var (otherUserId, _) = _t.AddUser("Ayşe", "ayse@ornek.local");

        Assert.DoesNotContain(await _categories.GetOwnAsync(otherUserId), c => c.Name == "Benim");
    }
}
