using System.Globalization;
using System.Text.Json;
using Takvim.Data;

namespace Takvim.Server.CalDav;

/// <summary>CalDAV sunucusunun hangi arayüzde dinleyeceği.</summary>
public enum CalDavBinding
{
    /// <summary>Yalnızca bu bilgisayar (127.0.0.1). Aynı makinedeki Thunderbird bağlanabilir.</summary>
    Loopback = 0,

    /// <summary>Yerel ağ (0.0.0.0). Telefon ve tablet bağlanabilir; parola zorunludur.</summary>
    LocalNetwork = 1,
}

/// <summary>
/// CalDAV ayarları.
/// <para>
/// Ayarlar veritabanında değil, ayrı bir dosyada tutulur: sunucunun hangi
/// adreste dinleyeceği uygulama <b>açılmadan önce</b> bilinmelidir, veritabanı
/// ise açıldıktan sonra hazır olur.
/// </para>
/// </summary>
public sealed record CalDavOptions
{
    /// <summary>
    /// Sunucu kapalıyken hiçbir port açılmaz. Varsayılan kapalıdır: kullanıcı
    /// istemeden ağa hiçbir şey açılmamalıdır.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Dinlenecek port. Sabit olmak zorunda: istemciler adresi bir kez kaydeder,
    /// her açılışta değişen bir port işe yaramaz.
    /// </summary>
    public int Port { get; init; } = 5232;

    public CalDavBinding Binding { get; init; } = CalDavBinding.Loopback;

    /// <summary>Kestrel'e verilecek adres.</summary>
    public string ListenUrl => Binding == CalDavBinding.LocalNetwork
        ? $"http://0.0.0.0:{Port.ToString(CultureInfo.InvariantCulture)}"
        : $"http://127.0.0.1:{Port.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Kullanıcıya gösterilecek, istemciye yazılacak adres.</summary>
    public string ClientUrl(string host) => $"http://{host}:{Port.ToString(CultureInfo.InvariantCulture)}/dav/";

    private static string FilePath => Path.Combine(TakvimPaths.DataRoot, "caldav.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Ayarları okur. Dosya yoksa ya da bozuksa varsayılana düşer.</summary>
    public static CalDavOptions Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new CalDavOptions();

            var text = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<CalDavOptions>(text, Json) ?? new CalDavOptions();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Bozuk ayar dosyası uygulamanın açılmasını engellememeli;
            // güvenli varsayılan sunucunun kapalı olmasıdır.
            return new CalDavOptions();
        }
    }

    public void Save()
    {
        TakvimPaths.EnsureCreated();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Port geçerli aralıkta mı; ayrıcalıklı portlar dışlanır.</summary>
    public static bool IsValidPort(int port) => port is >= 1024 and <= 65535;
}
