using System.Text.Json;
using Takvim.Core.Domain;
using Takvim.Data;

namespace Takvim.Server.Mail;

/// <summary>Tek bir sağlayıcı için istemci kimliği.</summary>
public sealed record ProviderCredentials
{
    public string ClientId { get; init; } = string.Empty;

    /// <summary>
    /// Google'ın masaüstü istemcilerinde verdiği "secret". Adı yanıltıcıdır:
    /// program kullanıcının diskinde olduğu için gerçek bir sır olamaz ve
    /// OAuth bunu kabul eder — güvenliği PKCE sağlar. Microsoft masaüstü
    /// istemcilerinde bu alan hiç istenmez.
    /// </summary>
    public string? ClientSecret { get; init; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);
}

/// <summary>
/// Posta kutusu bağlama ayarları.
/// <para>
/// İstemci kimliği veritabanına değil ayrı bir dosyaya yazılır: bu bilgi
/// kullanıcının verisi değil, uygulamanın yapılandırmasıdır ve veritabanı
/// yedeğiyle birlikte taşınması gerekmez. Dosya yolu
/// <c>%LOCALAPPDATA%\Takvim\posta.json</c>.
/// </para>
/// </summary>
public sealed record MailOAuthOptions
{
    public ProviderCredentials Google { get; init; } = new();
    public ProviderCredentials Microsoft { get; init; } = new();

    /// <summary>
    /// Otomatik tarama sıklığı, dakika. Sıfır ise yalnızca elle taranır.
    /// </summary>
    public int ScanIntervalMinutes { get; init; } = 30;

    public ProviderCredentials For(MailProvider provider)
        => provider == MailProvider.Microsoft ? Microsoft : Google;

    public bool IsConfigured(MailProvider provider) => For(provider).IsConfigured;

    /// <summary>Hiçbir sağlayıcı ayarlanmamışsa arayüz kurulum yönergesini gösterir.</summary>
    public bool AnyConfigured => Google.IsConfigured || Microsoft.IsConfigured;

    // ------------------------------------------------------------------

    private static string FilePath => Path.Combine(TakvimPaths.DataRoot, "posta.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Ayarları okur. Dosya yoksa ya da bozuksa boş yapılandırmaya düşer.</summary>
    public static MailOAuthOptions Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new MailOAuthOptions();

            return JsonSerializer.Deserialize<MailOAuthOptions>(File.ReadAllText(FilePath), Json)
                   ?? new MailOAuthOptions();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Bozuk ayar dosyası uygulamayı açılamaz kılmamalı; güvenli
            // varsayılan, posta bağlamanın kapalı olmasıdır.
            return new MailOAuthOptions();
        }
    }

    public void Save()
    {
        TakvimPaths.EnsureCreated();
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Kullanıcıya gösterilecek dosya yolu.</summary>
    public static string ConfigPath => FilePath;
}
