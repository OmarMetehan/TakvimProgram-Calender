using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;

namespace Takvim.Data.Services;

/// <summary>Ek yükleme sonucu.</summary>
public sealed record AttachmentResult(Attachment? Attachment, string? Error)
{
    public bool Success => Attachment is not null;
}

/// <summary>
/// Etkinlik eklerini yönetir.
/// <para>
/// Dosya içeriği diskte, üst verisi veritabanında tutulur. İkisi ayrı olduğu
/// için tutarsızlık mümkündür: kayıt var ama dosya yok, ya da tersi. Bu yüzden
/// silme <b>önce veritabanından</b> kaldırır, sonra dosyayı siler — dosya silme
/// başarısız olsa bile kullanıcı yetim bir kayıt görmez, yalnızca diskte
/// artık kimsenin göstermediği bir dosya kalır.
/// </para>
/// </summary>
/// <param name="storageRoot">
/// Dosyaların yazılacağı dizin. Yalnızca sınamalar geçer; uygulamada
/// <see cref="TakvimPaths.AttachmentsRoot"/> kullanılır. Sınamalar gerçek kökü
/// kullansaydı <see cref="PurgeOrphansAsync"/> kullanıcının dosyalarını silerdi.
/// </param>
public sealed class AttachmentService(TakvimDbContext db, IClock clock, string? storageRoot = null)
{
    private readonly string _root = storageRoot ?? TakvimPaths.AttachmentsRoot;

    /// <summary>Tek bir ekin en büyük boyutu.</summary>
    public const long MaxFileBytes = 25 * 1024 * 1024;

    /// <summary>Bir etkinliğe eklenebilecek en fazla dosya sayısı.</summary>
    public const int MaxPerEvent = 20;

    /// <summary>
    /// Kabul edilmeyen uzantılar. Bu bir güvenlik duvarı değil, kaza önleyicidir:
    /// takvim eki olarak çalıştırılabilir dosya paylaşmanın meşru bir nedeni yok.
    /// </summary>
    private static readonly string[] BlockedExtensions =
    [
        ".exe", ".msi", ".bat", ".cmd", ".com", ".scr", ".ps1", ".vbs", ".js", ".jar", ".dll",
    ];

    public Task<List<Attachment>> GetAsync(Guid eventId, CancellationToken ct = default)
        => db.Attachments
            .AsNoTracking()
            .Where(a => a.EventId == eventId)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct);

    /// <summary>Bir etkinliğin eklerinin toplam boyutu.</summary>
    public async Task<long> TotalSizeAsync(Guid eventId, CancellationToken ct = default)
        => await db.Attachments
            .Where(a => a.EventId == eventId)
            .SumAsync(a => a.SizeBytes, ct).ConfigureAwait(false);

    /// <summary>Dosyayı diske yazar ve kaydını açar.</summary>
    public async Task<AttachmentResult> AddAsync(
        Guid eventId,
        string fileName,
        string contentType,
        Stream content,
        Guid? uploadedByUserId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);

        // Dosya adı dizin ayracı içerebilir; yalnızca son parça kullanılır.
        var safeName = Path.GetFileName(fileName).Trim();
        if (safeName.Length == 0) return new AttachmentResult(null, "Dosya adı geçersiz.");

        var extension = Path.GetExtension(safeName).ToLowerInvariant();
        if (BlockedExtensions.Contains(extension))
        {
            return new AttachmentResult(null, $"Bu dosya türü eklenemez: {extension}");
        }

        var count = await db.Attachments.CountAsync(a => a.EventId == eventId, ct).ConfigureAwait(false);
        if (count >= MaxPerEvent)
        {
            return new AttachmentResult(null, $"Bir etkinliğe en fazla {MaxPerEvent} dosya eklenebilir.");
        }

        Directory.CreateDirectory(_root);

        var storageName = Guid.CreateVersion7().ToString("N") + extension;
        var path = Path.Combine(_root, storageName);

        long written;
        try
        {
            await using var file = File.Create(path);
            written = await CopyLimitedAsync(content, file, MaxFileBytes, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            return new AttachmentResult(null, "Dosya kaydedilemedi: " + ex.Message);
        }

        if (written < 0)
        {
            // Sınır aşıldı; yarım dosya diskte bırakılmaz.
            TryDelete(path);
            return new AttachmentResult(null, $"Dosya çok büyük (en fazla {Attachment.FormatSize(MaxFileBytes)}).");
        }

        var attachment = new Attachment
        {
            EventId = eventId,
            FileName = safeName,
            ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
            SizeBytes = written,
            StorageName = storageName,
            UploadedByUserId = uploadedByUserId,
            CreatedAt = clock.GetCurrentInstant().ToDateTimeOffset(),
        };

        db.Attachments.Add(attachment);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new AttachmentResult(attachment, null);
    }

    /// <summary>Ekin diskteki yolu. Dosya yoksa null döner.</summary>
    public async Task<(string Path, Attachment Record)?> OpenAsync(
        Guid attachmentId, CancellationToken ct = default)
    {
        var record = await db.Attachments
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == attachmentId, ct).ConfigureAwait(false);

        if (record is null) return null;

        var path = Path.Combine(_root, record.StorageName);
        return File.Exists(path) ? (path, record) : null;
    }

    /// <summary>Eki kaldırır.</summary>
    public async Task RemoveAsync(Guid attachmentId, CancellationToken ct = default)
    {
        var record = await db.Attachments
            .FirstOrDefaultAsync(a => a.Id == attachmentId, ct).ConfigureAwait(false);

        if (record is null) return;

        db.Attachments.Remove(record);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Kayıt gittikten sonra dosya silinir: sıra tersine olsaydı, dosya
        // silinip veritabanı yazması başarısız olduğunda kullanıcı açılamayan
        // bir ek görürdü.
        TryDelete(Path.Combine(_root, record.StorageName));
    }

    /// <summary>
    /// Hiçbir kayda bağlı olmayan dosyaları siler. Çöp kutusu temizliğiyle
    /// birlikte çalıştırılır: etkinlik kalıcı silinince ekleri de kaydı gider,
    /// dosyaları burada toplanır.
    /// </summary>
    public async Task<int> PurgeOrphansAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_root)) return 0;

        var known = await db.Attachments
            .AsNoTracking()
            .Select(a => a.StorageName)
            .ToListAsync(ct).ConfigureAwait(false);

        var live = known.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;

        foreach (var path in Directory.EnumerateFiles(_root))
        {
            if (live.Contains(Path.GetFileName(path))) continue;
            if (TryDelete(path)) removed++;
        }

        return removed;
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Akışı sınırlı boyutta kopyalar. Sınır aşılırsa -1 döner; böylece
    /// "boyutu önce oku, sonra kopyala" yarışı oluşmaz — istemcinin bildirdiği
    /// boyuta güvenmek yerine gerçekten yazılan bayt sayılır.
    /// </summary>
    private static async Task<long> CopyLimitedAsync(
        Stream source, Stream destination, long limit, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;

            total += read;
            if (total > limit) return -1;

            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        return total;
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
            // Dosya kilitliyse bir dahaki temizlikte alınır.
            return false;
        }
    }
}
