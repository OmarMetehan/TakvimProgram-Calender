using System.IO.Compression;
using NodaTime;
using Takvim.Data.Services;

namespace Takvim.Data.Tests;

/// <summary>
/// Yedekleme ve geri yükleme. İki nokta ayrıca sınanır: yedeğin gerçekten
/// açılabilir bir veritabanı içermesi, ve geri yüklemenin çalışan uygulamanın
/// altından dosya değiştirmemesi — hazırlık ile uygulama ayrı adımlardır.
/// </summary>
public class BackupTests : IDisposable
{
    private readonly TestDatabase _t = new();

    /// <summary>Sınamaya özel kökler; gerçek veri klasörüne dokunulmaz.</summary>
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "takvim-yedek-sinama", Guid.CreateVersion7().ToString("N"));

    private readonly BackupService _backups;

    private string BackupsRoot => Path.Combine(_root, "yedekler");
    private string AttachmentsRoot => Path.Combine(_root, "ekler");
    private string DatabaseFile => Path.Combine(_root, "takvim.db");

    public BackupTests()
    {
        Directory.CreateDirectory(BackupsRoot);
        Directory.CreateDirectory(AttachmentsRoot);

        _backups = new BackupService(_t.Db, _t.Clock, BackupsRoot);
    }

    public void Dispose()
    {
        _t.Dispose();

        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);

        GC.SuppressFinalize(this);
    }

    private Task<BackupResult> CreateAsync() => _backups.CreateAsync(AttachmentsRoot);

    private async Task SeedEventAsync(string title = "Toplantı")
    {
        await _t.Events.CreateAsync(_t.Input("2026-03-02 10:00", "2026-03-02 11:00", title));
        _t.Detach();
    }

    // ==================================================================
    // Yedek alma
    // ==================================================================

    [Fact]
    public async Task Yedek_alinir()
    {
        await SeedEventAsync();

        var result = await CreateAsync();

        Assert.True(result.Success);
        Assert.True(File.Exists(result.Backup!.Path));
        Assert.True(result.Backup.SizeBytes > 0);
    }

    [Fact]
    public async Task Yedegin_adi_tarihi_tasir()
    {
        // Sınama saati 2026-01-01 09:00 UTC.
        var result = await CreateAsync();

        Assert.StartsWith("takvim-yedek-2026-01-01", result.Backup!.Name, StringComparison.Ordinal);
        Assert.EndsWith(".zip", result.Backup.Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Yedek_veritabani_ve_kunye_icerir()
    {
        await SeedEventAsync();
        var result = await CreateAsync();

        using var zip = ZipFile.OpenRead(result.Backup!.Path);

        Assert.NotNull(zip.GetEntry("takvim.db"));
        Assert.NotNull(zip.GetEntry("kunye.json"));
    }

    [Fact]
    public async Task Kunye_sayilari_dogru()
    {
        await SeedEventAsync("Bir");
        await SeedEventAsync("İki");

        var result = await CreateAsync();
        var manifest = BackupService.ReadManifest(result.Backup!.Path);

        Assert.NotNull(manifest);
        Assert.Equal(2, manifest.EventCount);
        Assert.Equal(1, manifest.CalendarCount);
        Assert.Equal(1, manifest.Version);
    }

    [Fact]
    public async Task Yedekteki_veritabani_gercekten_acilabilir()
    {
        // Düz dosya kopyası WAL'daki son yazmaları kaçırırdı; VACUUM INTO
        // tutarlı bir anlık görüntü üretmeli.
        await SeedEventAsync("Kaydedilmiş toplantı");

        var result = await CreateAsync();
        var extracted = Path.Combine(_root, "cikarilan.db");

        using (var zip = ZipFile.OpenRead(result.Backup!.Path))
        {
            zip.GetEntry("takvim.db")!.ExtractToFile(extracted);
        }

        // Havuzlama kapatılır; açık kalan bir havuz dosyayı tutar ve sınama
        // sonunda klasör silinemez.
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={extracted};Pooling=False");

        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Title FROM Events LIMIT 1";

        Assert.Equal("Kaydedilmiş toplantı", await command.ExecuteScalarAsync() as string);
    }

    [Fact]
    public async Task Ekler_yedege_girer()
    {
        await File.WriteAllTextAsync(Path.Combine(AttachmentsRoot, "belge.txt"), "içerik");

        var result = await CreateAsync();

        using var zip = ZipFile.OpenRead(result.Backup!.Path);

        Assert.NotNull(zip.GetEntry("ekler/belge.txt"));
    }

    [Fact]
    public async Task Ek_klasoru_yoksa_yedek_yine_alinir()
    {
        Directory.Delete(AttachmentsRoot, recursive: true);

        Assert.True((await CreateAsync()).Success);
    }

    // ==================================================================
    // Listeleme ve temizlik
    // ==================================================================

    [Fact]
    public async Task Yedekler_listelenir()
    {
        await CreateAsync();
        _t.Clock.Advance(Duration.FromMinutes(1));
        await CreateAsync();

        Assert.Equal(2, _backups.List().Count);
    }

    [Fact]
    public async Task Yedekler_yeniden_eskiye_siralanir()
    {
        await CreateAsync();
        _t.Clock.Advance(Duration.FromDays(1));
        var newest = await CreateAsync();

        Assert.Equal(newest.Backup!.Name, _backups.List()[0].Name);
    }

    [Fact]
    public async Task Eski_yedekler_temizlenir()
    {
        for (var i = 0; i < 5; i++)
        {
            await CreateAsync();
            _t.Clock.Advance(Duration.FromMinutes(1));
        }

        Assert.Equal(3, _backups.Prune(keep: 2));
        Assert.Equal(2, _backups.List().Count);
    }

    [Fact]
    public async Task Temizlik_en_az_bir_yedek_birakir()
    {
        await CreateAsync();

        _backups.Prune(keep: 0);

        Assert.Single(_backups.List());
    }

    [Fact]
    public async Task Yedek_silinir()
    {
        var result = await CreateAsync();

        _backups.Delete(result.Backup!.Path);

        Assert.Empty(_backups.List());
    }

    [Fact]
    public async Task Kendi_klasorumuz_disindaki_dosya_silinmez()
    {
        var outside = Path.Combine(_root, "baska.zip");
        await File.WriteAllTextAsync(outside, "dokunma");

        _backups.Delete(outside);

        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task Bugun_yedek_alinip_alinmadigi_bilinir()
    {
        var today = new LocalDate(2026, 1, 1);

        Assert.False(_backups.HasBackupOn(today));

        await CreateAsync();

        Assert.True(_backups.HasBackupOn(today));
    }

    // ==================================================================
    // Künye doğrulama
    // ==================================================================

    [Fact]
    public async Task Yedek_olmayan_zip_taninir()
    {
        var fake = Path.Combine(BackupsRoot, "sahte.zip");

        using (var zip = ZipFile.Open(fake, ZipArchiveMode.Create))
        {
            zip.CreateEntry("baska.txt");
        }

        await Task.CompletedTask;

        Assert.Null(BackupService.ReadManifest(fake));
    }

    [Fact]
    public async Task Bozuk_dosya_cokertmez()
    {
        var broken = Path.Combine(BackupsRoot, "bozuk.zip");
        await File.WriteAllTextAsync(broken, "bu bir zip değil");

        Assert.Null(BackupService.ReadManifest(broken));
    }

    // ==================================================================
    // Geri yükleme
    // ==================================================================

    [Fact]
    public async Task Geri_yukleme_hazirlanir_ama_hemen_uygulanmaz()
    {
        var result = await CreateAsync();

        // Çalışan uygulamanın altından veritabanı değiştirilmemeli.
        Assert.Null(BackupService.StageRestore(result.Backup!.Path, _root));

        Assert.True(BackupService.HasPendingRestore(_root));
        Assert.False(File.Exists(DatabaseFile));
    }

    [Fact]
    public async Task Bekleyen_geri_yukleme_uygulanir()
    {
        await SeedEventAsync("Yedekteki toplantı");
        var result = await CreateAsync();

        BackupService.StageRestore(result.Backup!.Path, _root);

        Assert.True(BackupService.ApplyPendingRestore(_root));
        Assert.True(File.Exists(DatabaseFile));
        Assert.False(BackupService.HasPendingRestore(_root));
    }

    [Fact]
    public async Task Geri_yuklemede_eski_veritabani_saklanir()
    {
        // Yanlış yedekle geri yüklendiyse kullanıcının elinde kopya kalmalı.
        await File.WriteAllTextAsync(DatabaseFile, "eski veritabanı");

        var result = await CreateAsync();
        BackupService.StageRestore(result.Backup!.Path, _root);
        BackupService.ApplyPendingRestore(_root);

        var kept = Directory.GetFiles(_root, "takvim.onceki-*.db");

        Assert.Single(kept);
        Assert.Equal("eski veritabanı", await File.ReadAllTextAsync(kept[0]));
    }

    [Fact]
    public async Task Geri_yukleme_wal_dosyalarini_temizler()
    {
        // WAL eski veritabanına aittir; kalırsa yenisiyle uyuşmaz.
        await File.WriteAllTextAsync(DatabaseFile, "eski");
        await File.WriteAllTextAsync(DatabaseFile + "-wal", "kalinti");
        await File.WriteAllTextAsync(DatabaseFile + "-shm", "kalinti");

        var result = await CreateAsync();
        BackupService.StageRestore(result.Backup!.Path, _root);
        BackupService.ApplyPendingRestore(_root);

        Assert.False(File.Exists(DatabaseFile + "-wal"));
        Assert.False(File.Exists(DatabaseFile + "-shm"));
    }

    [Fact]
    public async Task Geri_yukleme_var_olan_ekleri_silmez()
    {
        await File.WriteAllTextAsync(Path.Combine(AttachmentsRoot, "yedekte-var.txt"), "eski");

        var result = await CreateAsync();

        // Yedek alındıktan sonra eklenen dosya.
        await File.WriteAllTextAsync(Path.Combine(AttachmentsRoot, "sonradan.txt"), "yeni");

        BackupService.StageRestore(result.Backup!.Path, _root);
        BackupService.ApplyPendingRestore(_root);

        // Yedekte olmayan bir dosyayı atmak veri kaybı olurdu.
        Assert.True(File.Exists(Path.Combine(AttachmentsRoot, "sonradan.txt")));
        Assert.True(File.Exists(Path.Combine(AttachmentsRoot, "yedekte-var.txt")));
    }

    [Fact]
    public void Bekleyen_yokken_uygulama_bir_sey_yapmaz()
        => Assert.False(BackupService.ApplyPendingRestore(_root));

    [Fact]
    public async Task Bekleyen_geri_yukleme_iptal_edilir()
    {
        var result = await CreateAsync();

        BackupService.StageRestore(result.Backup!.Path, _root);
        BackupService.CancelPendingRestore(_root);

        Assert.False(BackupService.HasPendingRestore(_root));
        Assert.False(BackupService.ApplyPendingRestore(_root));
    }

    [Fact]
    public void Yedek_olmayan_dosya_geri_yuklenemez()
    {
        var fake = Path.Combine(_root, "sahte.zip");
        File.WriteAllText(fake, "bu bir yedek değil");

        Assert.NotNull(BackupService.StageRestore(fake, _root));
        Assert.False(BackupService.HasPendingRestore(_root));
    }

    [Fact]
    public void Olmayan_dosya_geri_yuklenemez()
        => Assert.NotNull(BackupService.StageRestore(
            Path.Combine(_root, "yok.zip"), _root));

    [Fact]
    public void Bos_yol_reddedilir()
        => Assert.Throws<ArgumentException>(() => BackupService.StageRestore("   ", _root));
}
