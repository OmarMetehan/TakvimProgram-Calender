using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;

namespace Takvim.Data.Services;

/// <summary>Yedeğin içindeki künye. Geri yüklemeden önce okunur.</summary>
public sealed record BackupManifest
{
    /// <summary>Yedek biçiminin sürümü. Gelecekte biçim değişirse ayırt etmeye yarar.</summary>
    public int Version { get; init; } = 1;

    public DateTimeOffset CreatedAt { get; init; }

    public int EventCount { get; init; }
    public int CalendarCount { get; init; }
    public int AttachmentCount { get; init; }

    /// <summary>Yedeği alan uygulamanın sürümü; sorun ararken işe yarar.</summary>
    public string? AppVersion { get; init; }
}

/// <summary>Diskteki bir yedek dosyası.</summary>
/// <param name="Path">Tam yol.</param>
/// <param name="Name">Dosya adı.</param>
/// <param name="SizeBytes">Boyut.</param>
/// <param name="CreatedAt">Oluşturulma anı.</param>
public sealed record BackupFile(string Path, string Name, long SizeBytes, DateTimeOffset CreatedAt)
{
    /// <summary>Okunur boyut.</summary>
    public string SizeText => Attachment.FormatSize(SizeBytes);
}

/// <summary>Yedekleme ya da geri yükleme sonucu.</summary>
public sealed record BackupResult(BackupFile? Backup, string? Error)
{
    public bool Success => Error is null;
}

/// <summary>
/// Yedekleme ve geri yükleme.
/// <para>
/// Yedek tek bir zip dosyasıdır: veritabanı, ekler ve bir künye. Tek dosya
/// olması taşımayı kolaylaştırır — kullanıcı onu bir buluta ya da harici diske
/// koyup unutabilir.
/// </para>
/// <para>
/// Veritabanı kopyalanırken <c>VACUUM INTO</c> kullanılır. Dosyayı düz
/// kopyalamak yanlış olurdu: SQLite WAL kipinde çalışır ve son yazmalar ayrı
/// bir dosyada bekliyor olabilir; düz kopya onları kaçırır. <c>VACUUM INTO</c>
/// ise uygulama çalışırken bile tutarlı bir anlık görüntü üretir.
/// </para>
/// </summary>
public sealed class BackupService(TakvimDbContext db, IClock clock, string? backupsRoot = null)
{
    /// <summary>Zip içindeki veritabanının adı.</summary>
    private const string DatabaseEntry = "takvim.db";

    /// <summary>Zip içindeki eklerin klasörü.</summary>
    private const string AttachmentsPrefix = "ekler/";

    /// <summary>Zip içindeki künye dosyası.</summary>
    private const string ManifestEntry = "kunye.json";

    /// <summary>Otomatik yedeklerde saklanacak dosya sayısı.</summary>
    public const int DefaultKeepCount = 14;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _root = backupsRoot ?? TakvimPaths.BackupsRoot;

    // ==================================================================
    // Yedek alma
    // ==================================================================

    /// <summary>
    /// Yedek alır. Uygulama çalışırken güvenlidir.
    /// </summary>
    /// <param name="attachmentsRoot">Eklerin bulunduğu klasör; sınamalar geçer.</param>
    public async Task<BackupResult> CreateAsync(
        string? attachmentsRoot = null, CancellationToken ct = default)
    {
        var attachments = attachmentsRoot ?? TakvimPaths.AttachmentsRoot;

        Directory.CreateDirectory(_root);

        var now = clock.GetCurrentInstant().ToDateTimeOffset();
        var path = Path.Combine(_root, FileNameFor(now));

        // Veritabanının anlık görüntüsü önce geçici bir dosyaya alınır; zip
        // doğrudan canlı dosyadan okunamaz.
        var snapshot = Path.Combine(Path.GetTempPath(), $"takvim-{Guid.CreateVersion7():N}.db");

        try
        {
            await SnapshotDatabaseAsync(snapshot, ct).ConfigureAwait(false);

            var manifest = new BackupManifest
            {
                CreatedAt = now,
                EventCount = await db.Events.CountAsync(e => e.DeletedAt == null, ct).ConfigureAwait(false),
                CalendarCount = await db.Calendars.CountAsync(c => c.DeletedAt == null, ct).ConfigureAwait(false),
                AttachmentCount = await db.Attachments.CountAsync(ct).ConfigureAwait(false),
                AppVersion = typeof(BackupService).Assembly.GetName().Version?.ToString(),
            };

            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(snapshot, DatabaseEntry, CompressionLevel.Optimal);

                if (Directory.Exists(attachments))
                {
                    foreach (var file in Directory.EnumerateFiles(attachments))
                    {
                        zip.CreateEntryFromFile(
                            file, AttachmentsPrefix + Path.GetFileName(file), CompressionLevel.Optimal);
                    }
                }

                var entry = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
                await using var stream = entry.Open();
                await JsonSerializer.SerializeAsync(stream, manifest, Json, ct).ConfigureAwait(false);
            }

            return new BackupResult(Describe(path), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(path);
            return new BackupResult(null, "Yedek alınamadı: " + ex.Message);
        }
        finally
        {
            TryDelete(snapshot);
        }
    }

    /// <summary>
    /// Veritabanının tutarlı bir kopyasını çıkarır.
    /// <para>
    /// Bellek içi veritabanlarında da çalışır; sınamalar buna dayanır.
    /// </para>
    /// </summary>
    private async Task SnapshotDatabaseAsync(string destination, CancellationToken ct)
    {
        // Dosya varsa VACUUM INTO reddeder.
        TryDelete(destination);

        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;

        if (wasClosed) await connection.OpenAsync(ct).ConfigureAwait(false);

        try
        {
            await using var command = connection.CreateCommand();

            // Yol tek tırnak içine konur; SQLite bu deyimde parametre kabul etmez.
            command.CommandText = $"VACUUM INTO '{destination.Replace("'", "''", StringComparison.Ordinal)}'";

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (wasClosed) await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    // ==================================================================
    // Listeleme
    // ==================================================================

    /// <summary>Diskteki yedekler; yeniden eskiye.</summary>
    public List<BackupFile> List()
    {
        if (!Directory.Exists(_root)) return [];

        return [.. Directory
            .EnumerateFiles(_root, "*.zip")
            .Select(Describe)
            .OrderByDescending(b => b.CreatedAt)];
    }

    /// <summary>Yedeğin künyesini okur. Okunamazsa dosya yedek değildir.</summary>
    public static BackupManifest? ReadManifest(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);

            var entry = zip.GetEntry(ManifestEntry);
            if (entry is null || zip.GetEntry(DatabaseEntry) is null) return null;

            using var stream = entry.Open();
            return JsonSerializer.Deserialize<BackupManifest>(stream, Json);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Yedeği siler.</summary>
    public void Delete(string path)
    {
        // Yalnızca kendi klasörümüzdeki dosyalar silinebilir.
        if (!IsInRoot(path)) return;

        TryDelete(path);
    }

    /// <summary>
    /// En yenilerden <paramref name="keep"/> tanesini bırakıp gerisini siler.
    /// Otomatik yedekler birikip diski doldurmasın diye.
    /// </summary>
    public int Prune(int keep = DefaultKeepCount)
    {
        var removed = 0;

        foreach (var backup in List().Skip(Math.Max(1, keep)))
        {
            if (TryDelete(backup.Path)) removed++;
        }

        return removed;
    }

    /// <summary>Bugün alınmış bir yedek var mı; otomatik yedekleme buna bakar.</summary>
    public bool HasBackupOn(LocalDate date)
        => List().Exists(b => LocalDate.FromDateTime(b.CreatedAt.LocalDateTime.Date) == date);

    // ==================================================================
    // Geri yükleme
    // ==================================================================

    /// <summary>
    /// Geri yüklemeyi hazırlar ama <b>uygulamaz</b>.
    /// <para>
    /// Çalışan bir uygulamanın altından veritabanını değiştirmek, açık
    /// bağlantıları bozar ve yarım yazmalara yol açar. Bunun yerine dosyalar
    /// bir bekleme klasörüne çıkarılır ve bir işaret bırakılır; uygulama bir
    /// sonraki açılışında, veritabanına hiç dokunmadan önce
    /// <see cref="ApplyPendingRestore"/> ile devralır.
    /// </para>
    /// </summary>
    /// <returns>Hazırlandıysa null, hazırlanamadıysa gerekçesi.</returns>
    public static string? StageRestore(string backupPath, string? dataRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);

        if (!File.Exists(backupPath)) return "Yedek dosyası bulunamadı.";
        if (ReadManifest(backupPath) is null) return "Bu dosya bir Takvim yedeği değil.";

        var root = dataRoot ?? TakvimPaths.DataRoot;
        var staging = PendingRoot(root);

        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);

            ZipFile.ExtractToDirectory(backupPath, staging);

            // İşaret en son yazılır: yarım çıkarılmış bir klasör devralınmamalı.
            File.WriteAllText(PendingMarker(root), backupPath);

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return "Yedek açılamadı: " + ex.Message;
        }
    }

    /// <summary>Bekleyen bir geri yükleme var mı.</summary>
    public static bool HasPendingRestore(string? dataRoot = null)
        => File.Exists(PendingMarker(dataRoot ?? TakvimPaths.DataRoot));

    /// <summary>Bekleyen geri yüklemeyi iptal eder.</summary>
    public static void CancelPendingRestore(string? dataRoot = null)
    {
        var root = dataRoot ?? TakvimPaths.DataRoot;

        TryDelete(PendingMarker(root));

        if (Directory.Exists(PendingRoot(root)))
        {
            try
            {
                Directory.Delete(PendingRoot(root), recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Bir sonraki denemede temizlenir.
            }
        }
    }

    /// <summary>
    /// Bekleyen geri yüklemeyi uygular. <b>Veritabanı açılmadan önce</b>
    /// çağrılmalıdır.
    /// <para>
    /// Mevcut veritabanı silinmez, yanına taşınır: geri yükleme yanlış yedekle
    /// yapıldıysa kullanıcının elinde hâlâ bir kopya kalır.
    /// </para>
    /// </summary>
    /// <returns>Bir şey uygulandıysa true.</returns>
    public static bool ApplyPendingRestore(string? dataRoot = null)
    {
        var root = dataRoot ?? TakvimPaths.DataRoot;

        if (!File.Exists(PendingMarker(root))) return false;

        var staging = PendingRoot(root);
        var restoredDb = Path.Combine(staging, DatabaseEntry);

        if (!File.Exists(restoredDb))
        {
            CancelPendingRestore(root);
            return false;
        }

        try
        {
            var databaseFile = Path.Combine(root, "takvim.db");

            // Eskisi silinmez, yanına taşınır.
            if (File.Exists(databaseFile))
            {
                var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                File.Move(databaseFile, Path.Combine(root, $"takvim.onceki-{stamp}.db"), overwrite: true);
            }

            // WAL ve paylaşılan bellek dosyaları eski veritabanına aittir;
            // kalırlarsa yenisiyle uyuşmaz.
            TryDelete(databaseFile + "-wal");
            TryDelete(databaseFile + "-shm");

            File.Move(restoredDb, databaseFile, overwrite: true);

            RestoreAttachments(staging, Path.Combine(root, "ekler"));

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            CancelPendingRestore(root);
        }
    }

    private static void RestoreAttachments(string staging, string attachmentsRoot)
    {
        var source = Path.Combine(staging, "ekler");
        if (!Directory.Exists(source)) return;

        Directory.CreateDirectory(attachmentsRoot);

        // Var olan ekler silinmez: yedekte olmayan bir dosya, yedek alındıktan
        // sonra eklenmiş olabilir ve onu atmak veri kaybıdır.
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var target = Path.Combine(attachmentsRoot, Path.GetFileName(file));

            if (!File.Exists(target)) File.Move(file, target);
        }
    }

    // ==================================================================

    private static string PendingRoot(string dataRoot) => Path.Combine(dataRoot, "geri-yukleme");

    private static string PendingMarker(string dataRoot) => Path.Combine(dataRoot, "geri-yukleme.isaret");

    /// <summary>Ad tarihe göre verilir; sıralama ve okunurluk birlikte gelsin.</summary>
    private static string FileNameFor(DateTimeOffset moment)
        => $"takvim-yedek-{moment.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture)}.zip";

    private static BackupFile Describe(string path)
    {
        var info = new FileInfo(path);

        // Tarih önce addan okunur: dosya damgası kopyalanınca ya da bir buluta
        // gidip gelince değişir, addaki tarih ise yedeğin gerçekten ne zaman
        // alındığını söyler.
        return new BackupFile(path, info.Name, info.Length, ParseDate(info.Name) ?? info.LastWriteTime);
    }

    /// <summary>Dosya adındaki tarihi çözer; ad beklenen kalıpta değilse null.</summary>
    private static DateTimeOffset? ParseDate(string fileName)
    {
        const string Prefix = "takvim-yedek-";

        if (!fileName.StartsWith(Prefix, StringComparison.Ordinal)) return null;

        var stamp = Path.GetFileNameWithoutExtension(fileName)[Prefix.Length..];

        return DateTimeOffset.TryParseExact(
            stamp, "yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    private bool IsInRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(_root);

        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
