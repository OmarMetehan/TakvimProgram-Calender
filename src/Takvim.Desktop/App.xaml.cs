using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Takvim.Data;
using Takvim.Server;
using Takvim.Server.State;

// Windows Forms tepsi simgesi için açıldı ve iki çerçeve de "Application"
// adını taşıyor. Kabuk WPF'tir; ad çakışması burada bir kez çözülür.
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

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
public partial class App : Application, IDisposable
{
    private WebApplication? _server;
    private TrayIcon? _tray;

    /// <summary>
    /// Kullanıcı gerçekten çıkmak istedi mi. Pencereyi kapatmak çıkmak değildir:
    /// hatırlatıcıların gelmesi için uygulamanın açık kalması gerekir.
    /// </summary>
    private bool _exiting;

    /// <summary>Tepsiye ilk inişte bir kez bilgi verilir; her seferinde değil.</summary>
    private bool _hintShown;

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

            SetUpTray(window);
            SubscribeToReminders(window);
        }
        catch (Exception ex)
        {
            ReportFatal(window, ex);
        }
    }

    /// <summary>
    /// Tepsi simgesini kurar ve pencerenin kapanışını ona bağlar.
    /// <para>
    /// Pencereyi kapatmak uygulamadan çıkmaz. Çıksaydı hatırlatıcılar da
    /// dururdu; oysa bir takvim uygulamasından beklenen tam tersidir. Çıkış
    /// tepsi menüsünden açıkça yapılır.
    /// </para>
    /// </summary>
    private void SetUpTray(MainWindow window)
    {
        _tray = new TrayIcon();

        _tray.OpenRequested += () => Dispatcher.Invoke(() => ShowWindow(window));
        _tray.ExitRequested += () => Dispatcher.Invoke(() =>
        {
            _exiting = true;
            Shutdown();
        });

        window.Closing += (_, e) =>
        {
            if (_exiting) return;

            e.Cancel = true;
            window.Hide();

            if (!_hintShown)
            {
                _hintShown = true;
                _tray?.ShowHiddenHint();
            }
        };
    }

    private static void ShowWindow(MainWindow window)
    {
        window.Show();

        // Simge durumundan geri getirilir; aksi hâlde görev çubuğunda kalır.
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;

        window.Activate();
    }

    /// <summary>
    /// Hatırlatıcı yayınını dinler ve pencere görünmüyorken Windows bildirimi
    /// gösterir. Pencere açıkken bildirim çıkmaz: arayüzdeki şerit zaten
    /// görünür ve ikisi birden gereksiz gürültü olur.
    /// </summary>
    private void SubscribeToReminders(MainWindow window)
    {
        if (_server is null) return;

        var broadcast = _server.Services.GetRequiredService<ReminderBroadcast>();

        broadcast.Fired += reminders => Dispatcher.Invoke(() =>
        {
            if (window.IsVisible && window.WindowState != WindowState.Minimized) return;

            _tray?.ShowReminders(reminders);
        });
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

    /// <summary>
    /// Tepsi simgesini bırakır. WPF <see cref="Application"/> nesnesini kendisi
    /// atmaz; bu yüzden çağrı <see cref="OnExit"/> içinden gelir.
    /// </summary>
    public void Dispose()
    {
        _tray?.Dispose();
        _tray = null;

        GC.SuppressFinalize(this);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        // Simge önce kaldırılır; sunucu kapanışını beklerse tepside asılı kalır.
        Dispose();

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
