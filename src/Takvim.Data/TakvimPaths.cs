namespace Takvim.Data;

/// <summary>Uygulamanın diskteki yerleşimi. Tek yerden tanımlanır ki yedekleme de aynı yolu bilsin.</summary>
public static class TakvimPaths
{
    /// <summary>Veri kökü: %LOCALAPPDATA%\Takvim</summary>
    public static string DataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Takvim");

    public static string DatabaseFile => Path.Combine(DataRoot, "takvim.db");

    /// <summary>Etkinlik ekleri; veritabanının şişmemesi için dosya sisteminde tutulur.</summary>
    public static string AttachmentsRoot => Path.Combine(DataRoot, "ekler");

    public static string BackupsRoot => Path.Combine(DataRoot, "yedekler");

    public static string ConnectionString => $"Data Source={DatabaseFile}";

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(AttachmentsRoot);
        Directory.CreateDirectory(BackupsRoot);
    }
}
