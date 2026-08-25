using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Takvim.Data;
using Takvim.Server;

namespace Takvim.Desktop;

/// <summary>
/// Masaüstü kabuğu. Uygulamanın kendisi ASP.NET Core'dur; burada yalnızca
/// yerel bir ağ arayüzünde başlatılıp bir pencere içinde gösterilir.
/// <para>
/// Sunucu <c>127.0.0.1</c> üzerinde ve işletim sisteminin verdiği rastgele bir
/// portta dinler. Sabit port kullanılmaz: aynı porta başka bir uygulama
/// bağlanmışsa açılış başarısız olur, ayrıca sabit port ağdaki başka
/// makinelerin bağlanmayı denemesini kolaylaştırır.
/// </para>
/// </summary>
public partial class App : Application
{
    private WebApplication? _server;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // İşlenmemiş arayüz hatası uygulamayı sessizce düşürmesin.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Aynı anda iki örnek çalışırsa ikisi de aynı SQLite dosyasına yazar.
        // WAL kipi bunu güvenli kılar, ancak pencereyi yine de tek tutmak yeterlidir.
        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        try
        {
            window.SetStatus("Veritabanı hazırlanıyor…");

            // Kabuk kendi wwwroot klasörünü kurar; Blazor'ın statik varlık bildirimi
            // kullanılmaz. Kullanılsaydı, önceden sıkıştırılmış kopyaları bulamayıp
            // tarayıcıya boş stil ve betik gönderirdi.
            _server = TakvimHost.Build(
                args: null, urls: "http://127.0.0.1:0", useStaticAssetManifest: false);
            await TakvimHost.InitializeDatabaseAsync(_server);

            window.SetStatus("Sunucu başlatılıyor…");
            await _server.StartAsync();

            var address = ResolveAddress(_server)
                ?? throw new InvalidOperationException("Sunucu adresi belirlenemedi.");

            await window.LoadAsync(address);
        }
        catch (Exception ex)
        {
            ReportFatal(window, ex);
        }
    }

    /// <summary>Kestrel'in gerçekten bağlandığı adresi okur; port 0 verildiği için önceden bilinmez.</summary>
    private static string? ResolveAddress(WebApplication server)
    {
        var addresses = server.Services
            .GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;

        return addresses?.FirstOrDefault();
    }

    private static void ReportFatal(MainWindow window, Exception ex)
    {
        var logPath = Path.Combine(TakvimPaths.DataRoot, "hata.log");

        try
        {
            File.AppendAllText(logPath,
                $"{DateTimeOffset.Now:O}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Günlük yazılamıyorsa da kullanıcıya haber vermeye devam ederiz.
        }

        window.SetStatus("Takvim başlatılamadı.");

        MessageBox.Show(
            $"Takvim başlatılamadı.\n\n{ex.Message}\n\nAyrıntılar: {logPath}",
            "Takvim", MessageBoxButton.OK, MessageBoxImage.Error);

        Current.Shutdown(1);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_server is not null)
        {
            // Bekleyen veritabanı yazmalarının tamamlanması için düzgün kapatılır.
            try
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _server.StopAsync(shutdown.Token);
                await _server.DisposeAsync();
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // Kapanış sırasında iptal edilmiş; yapılacak bir şey yok.
            }
        }

        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var logPath = Path.Combine(TakvimPaths.DataRoot, "hata.log");

        try
        {
            File.AppendAllText(logPath,
                $"{DateTimeOffset.Now:O} [arayüz]{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }

        MessageBox.Show(
            $"Beklenmeyen bir hata oluştu.\n\n{e.Exception.Message}\n\nAyrıntılar: {logPath}",
            "Takvim", MessageBoxButton.OK, MessageBoxImage.Error);

        e.Handled = true;
    }
}
