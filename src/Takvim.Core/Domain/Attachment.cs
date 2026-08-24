namespace Takvim.Core.Domain;

/// <summary>
/// Bir etkinliğe iliştirilmiş dosya.
/// <para>
/// Dosyanın içeriği veritabanında değil, disk üzerinde
/// <c>%LOCALAPPDATA%\Takvim\ekler</c> altında tutulur. SQLite büyük ikili
/// verileri saklayabilir, ama o zaman her yedek ve her sorgu bu yükü taşır;
/// ayrıca dosyaları elle kurtarmak imkânsızlaşır.
/// </para>
/// </summary>
public class Attachment
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid EventId { get; set; }
    public Event? Event { get; set; }

    /// <summary>Kullanıcının gördüğü ad.</summary>
    public required string FileName { get; set; }

    public required string ContentType { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>
    /// Diskteki dosyanın adı. Kullanıcının verdiği ad kullanılmaz: aynı adlı iki
    /// dosya çakışır, ayrıca dosya adı dizin ayracı içerebilir.
    /// </summary>
    public required string StorageName { get; set; }

    public Guid? UploadedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Okunur boyut: "142 KB", "2,4 MB".</summary>
    public string SizeText => FormatSize(SizeBytes);

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };

    /// <summary>Dosya türüne göre gösterilecek simge.</summary>
    public string Icon => ContentType switch
    {
        var t when t.StartsWith("image/", StringComparison.Ordinal) => "🖼",
        "application/pdf" => "📕",
        var t when t.Contains("word", StringComparison.Ordinal) => "📄",
        var t when t.Contains("sheet", StringComparison.Ordinal)
                || t.Contains("excel", StringComparison.Ordinal) => "📊",
        var t when t.Contains("presentation", StringComparison.Ordinal)
                || t.Contains("powerpoint", StringComparison.Ordinal) => "📽",
        var t when t.Contains("zip", StringComparison.Ordinal)
                || t.Contains("compressed", StringComparison.Ordinal) => "🗜",
        _ => "📎",
    };
}
