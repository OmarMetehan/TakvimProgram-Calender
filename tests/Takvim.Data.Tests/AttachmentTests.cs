using System.Text;
using Microsoft.EntityFrameworkCore;
using Takvim.Core.Domain;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// Etkinlik ekleri. Dosya diskte, üst veri veritabanında olduğu için asıl
/// sınanan şey ikisinin tutarlı kalması.
/// </summary>
public class AttachmentTests : IDisposable
{
    private readonly TestDatabase _t = new();
    private readonly AttachmentService _attachments;

    /// <summary>
    /// Sınamaya özel dosya kökü. Gerçek kök kullanılsaydı
    /// <see cref="AttachmentService.PurgeOrphansAsync"/> kullanıcının
    /// eklerini silerdi — sınama veritabanı boş olduğu için hepsi yetim görünür.
    /// </summary>
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "takvim-ek-sinama", Guid.CreateVersion7().ToString("N"));

    public AttachmentTests()
        => _attachments = new AttachmentService(_t.Db, _t.Clock, _root);

    public void Dispose()
    {
        _t.Dispose();

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }

    private async Task<Guid> CreateEventAsync()
    {
        var created = await _t.Events.CreateAsync(_t.Input("2026-03-02 10:00", "2026-03-02 11:00"));
        _t.Detach();
        return created.PrimaryEventId;
    }

    private static MemoryStream Content(string text) => new(Encoding.UTF8.GetBytes(text));

    private Task<AttachmentResult> AddAsync(Guid eventId, string name, string text = "içerik")
        => _attachments.AddAsync(eventId, name, "text/plain", Content(text), _t.UserId);

    // ==================================================================
    // Ekleme
    // ==================================================================

    [Fact]
    public async Task Dosya_eklenir_ve_diske_yazilir()
    {
        var eventId = await CreateEventAsync();

        var result = await AddAsync(eventId, "gündem.txt", "Toplantı gündemi");
        _t.Detach();

        Assert.True(result.Success);
        Assert.Equal("gündem.txt", result.Attachment!.FileName);
        Assert.Equal(Encoding.UTF8.GetByteCount("Toplantı gündemi"), result.Attachment.SizeBytes);

        var opened = await _attachments.OpenAsync(result.Attachment.Id);
        Assert.NotNull(opened);
        Assert.Equal("Toplantı gündemi", await File.ReadAllTextAsync(opened.Value.Path));
    }

    [Fact]
    public async Task Diskteki_ad_kullanicinin_verdigi_ad_degildir()
    {
        // Aynı adlı iki dosya çakışmamalı, ayrıca ad dizin ayracı içerebilir.
        var eventId = await CreateEventAsync();

        var first = await AddAsync(eventId, "rapor.txt", "bir");
        _t.Detach();
        var second = await AddAsync(eventId, "rapor.txt", "iki");
        _t.Detach();

        Assert.NotEqual(first.Attachment!.StorageName, second.Attachment!.StorageName);
        Assert.Equal(2, await _t.Db.Attachments.CountAsync());
    }

    [Fact]
    public async Task Dizin_ayraci_iceren_ad_temizlenir()
    {
        var eventId = await CreateEventAsync();

        var result = await AddAsync(eventId, @"..\..\gizli\parola.txt");
        _t.Detach();

        Assert.True(result.Success);
        Assert.Equal("parola.txt", result.Attachment!.FileName);
        Assert.DoesNotContain("..", result.Attachment.StorageName, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("kurulum.exe")]
    [InlineData("betik.ps1")]
    [InlineData("makro.BAT")]
    public async Task Calistirilabilir_dosya_reddedilir(string name)
    {
        var eventId = await CreateEventAsync();

        var result = await AddAsync(eventId, name);

        Assert.False(result.Success);
        Assert.Contains("eklenemez", result.Error, StringComparison.Ordinal);
        Assert.Empty(await _t.Db.Attachments.ToListAsync());
    }

    [Fact]
    public async Task Bos_ad_reddedilir()
    {
        var eventId = await CreateEventAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => AddAsync(eventId, "   "));
    }

    [Fact]
    public async Task Sinir_asan_dosya_reddedilir_ve_diskte_kalmaz()
    {
        var eventId = await CreateEventAsync();

        // Sınırdan bir bayt fazla.
        var big = new MemoryStream(new byte[AttachmentService.MaxFileBytes + 1]);
        var result = await _attachments.AddAsync(eventId, "büyük.bin", "application/octet-stream", big, _t.UserId);

        Assert.False(result.Success);
        Assert.Contains("çok büyük", result.Error, StringComparison.Ordinal);
        Assert.Empty(await _t.Db.Attachments.ToListAsync());

        // Yarım yazılmış dosya bırakılmamalı.
        Assert.Equal(0, await _attachments.PurgeOrphansAsync());
    }

    [Fact]
    public async Task Ek_sayisi_sinirlanir()
    {
        var eventId = await CreateEventAsync();

        for (var i = 0; i < AttachmentService.MaxPerEvent; i++)
        {
            Assert.True((await AddAsync(eventId, $"dosya-{i}.txt")).Success);
            _t.Detach();
        }

        var extra = await AddAsync(eventId, "fazla.txt");

        Assert.False(extra.Success);
        Assert.Contains("en fazla", extra.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tur_belirtilmezse_genel_tur_kullanilir()
    {
        var eventId = await CreateEventAsync();

        var result = await _attachments.AddAsync(eventId, "veri.bin", "", Content("x"), _t.UserId);

        Assert.Equal("application/octet-stream", result.Attachment!.ContentType);
    }

    // ==================================================================
    // Listeleme
    // ==================================================================

    [Fact]
    public async Task Ekler_etkinlige_gore_listelenir()
    {
        var first = await CreateEventAsync();
        var second = await CreateEventAsync();

        await AddAsync(first, "bir.txt");
        _t.Detach();
        await AddAsync(second, "iki.txt");
        _t.Detach();

        Assert.Single(await _attachments.GetAsync(first));
        Assert.Single(await _attachments.GetAsync(second));
    }

    [Fact]
    public async Task Toplam_boyut_hesaplanir()
    {
        var eventId = await CreateEventAsync();

        await AddAsync(eventId, "bir.txt", "12345");
        _t.Detach();
        await AddAsync(eventId, "iki.txt", "678");
        _t.Detach();

        Assert.Equal(8, await _attachments.TotalSizeAsync(eventId));
    }

    // ==================================================================
    // Silme ve tutarlılık
    // ==================================================================

    [Fact]
    public async Task Kaldirilan_ek_diskten_de_silinir()
    {
        var eventId = await CreateEventAsync();
        var result = await AddAsync(eventId, "gündem.txt");
        _t.Detach();

        var path = Path.Combine(_root, result.Attachment!.StorageName);
        Assert.True(File.Exists(path));

        await _attachments.RemoveAsync(result.Attachment.Id);
        _t.Detach();

        Assert.Empty(await _t.Db.Attachments.ToListAsync());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Olmayan_ek_kaldirilinca_hata_vermez()
        => await _attachments.RemoveAsync(Guid.NewGuid());

    [Fact]
    public async Task Diskte_olmayan_ek_acilamaz()
    {
        var eventId = await CreateEventAsync();
        var result = await AddAsync(eventId, "gündem.txt");
        _t.Detach();

        // Dosya dışarıdan silinirse kayıt yetim kalır; açma null dönmeli.
        File.Delete(Path.Combine(_root, result.Attachment!.StorageName));

        Assert.Null(await _attachments.OpenAsync(result.Attachment.Id));
    }

    [Fact]
    public async Task Yetim_dosyalar_temizlenir()
    {
        var eventId = await CreateEventAsync();
        var result = await AddAsync(eventId, "gündem.txt");
        _t.Detach();

        // Kayıt doğrudan silinirse dosya yetim kalır.
        _t.Db.Attachments.RemoveRange(_t.Db.Attachments);
        await _t.Db.SaveChangesAsync();
        _t.Detach();

        var path = Path.Combine(_root, result.Attachment!.StorageName);
        Assert.True(File.Exists(path));

        Assert.True(await _attachments.PurgeOrphansAsync() >= 1);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Etkinlik_silinince_ek_kaydi_da_gider()
    {
        var eventId = await CreateEventAsync();
        await AddAsync(eventId, "gündem.txt");
        _t.Detach();

        // Önce çöp kutusuna, sonra saklama süresi dolunca kalıcı silme.
        await _t.Events.DeleteAsync(eventId, null, SeriesEditScope.AllInSeries, _t.UserId);
        _t.Detach();

        _t.Clock.Advance(NodaTime.Duration.FromDays(31));
        Assert.Equal(1, await _t.Events.PurgeTrashAsync());
        _t.Detach();

        Assert.Empty(await _t.Db.Attachments.ToListAsync());
    }

    // ==================================================================
    // Biçimlendirme
    // ==================================================================

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(1536, "1,5 KB")]
    [InlineData(5 * 1024 * 1024, "5 MB")]
    public void Boyut_okunur_bicimde_gosterilir(long bytes, string expected)
        => Assert.Equal(expected, Attachment.FormatSize(bytes));

    [Theory]
    [InlineData("image/png", "🖼")]
    [InlineData("application/pdf", "📕")]
    [InlineData("application/zip", "🗜")]
    [InlineData("text/plain", "📎")]
    public void Simge_dosya_turune_gore_secilir(string contentType, string expected)
    {
        var attachment = new Attachment
        {
            FileName = "x", ContentType = contentType, StorageName = "x",
        };

        Assert.Equal(expected, attachment.Icon);
    }
}
