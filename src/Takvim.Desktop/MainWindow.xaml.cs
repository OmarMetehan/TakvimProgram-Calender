using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Takvim.Data;

namespace Takvim.Desktop;

/// <summary>
/// Uygulama penceresi. İçinde yalnızca bir WebView2 vardır; arayüzün tamamı
/// gömülü sunucudan gelir.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    public void SetStatus(string message) => SplashText.Text = message;

    /// <summary>Tarayıcı bileşenini hazırlar ve uygulamayı yükler.</summary>
    public async Task LoadAsync(string address)
    {
        SetStatus("Arayüz yükleniyor…");

        // Tarayıcı verileri (yerel depolama, önbellek) uygulamanın kendi
        // klasöründe tutulur; kullanıcının Edge profiline karışmaz.
        var environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: Path.Combine(TakvimPaths.DataRoot, "webview"));

        await Browser.EnsureCoreWebView2Async(environment);

        var settings = Browser.CoreWebView2.Settings;
        settings.AreDefaultContextMenusEnabled = false;   // sağ tık menüsü uygulamaya ait değil
        settings.IsStatusBarEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false; // F5, Ctrl+P gibi tuşlar uygulamaya bırakılır
        settings.IsSwipeNavigationEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;

#if DEBUG
        settings.AreDevToolsEnabled = true;
#else
        settings.AreDevToolsEnabled = false;
#endif

        // Dış bağlantılar (toplantı adresleri) uygulama penceresinde değil,
        // kullanıcının varsayılan tarayıcısında açılır.
        Browser.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
        Browser.NavigationCompleted += OnNavigationCompleted;

        Browser.CoreWebView2.Navigate(address);
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            SetStatus($"Arayüz yüklenemedi ({e.WebErrorStatus}).");
            return;
        }

        Splash.Visibility = Visibility.Collapsed;
        Browser.Visibility = Visibility.Visible;

        // İlk yüklemeden sonra bu işi yapmaya gerek yok.
        Browser.NavigationCompleted -= OnNavigationCompleted;

        await WriteDiagnosticsAsync();
    }

    /// <summary>
    /// Sayfanın gerçekten biçimlenip biçimlenmediğini <b>tarayıcının içinden</b>
    /// ölçüp dosyaya yazar.
    /// <para>
    /// Bunun dışarıdan ölçülemediği bir kez yaşandı: sunucu dosyaları doğru
    /// gönderdiği hâlde arayüz biçimsiz açıldı. HTTP yanıtına bakmak yetmiyor,
    /// sayfanın stili uygulayıp uygulamadığına bakmak gerekiyor.
    /// </para>
    /// </summary>
    private async Task WriteDiagnosticsAsync()
    {
        const string Script = @"
            (function () {
                var sheets = [];
                for (var i = 0; i < document.styleSheets.length; i++) {
                    var sheet = document.styleSheets[i];
                    var rules = -1;
                    try { rules = sheet.cssRules ? sheet.cssRules.length : -1; } catch (e) { rules = -2; }
                    sheets.push({ adres: sheet.href, kural: rules });
                }

                var body = getComputedStyle(document.body);

                return JSON.stringify({
                    adres: location.href,
                    stilSayisi: document.styleSheets.length,
                    stiller: sheets,
                    yaziTipi: body.fontFamily,
                    zemin: body.backgroundColor,
                    blazorYuklendi: typeof Blazor !== 'undefined',
                    takvimYuklendi: typeof window.takvim !== 'undefined',
                    kenarVarMi: !!document.querySelector('.kenar')
                }, null, 2);
            })()";

        try
        {
            var json = await Browser.CoreWebView2.ExecuteScriptAsync(Script);

            // Sonuç JSON dizesi olarak sarmalanmış gelir; kaydetmeden çözülür.
            var text = System.Text.Json.JsonSerializer.Deserialize<string>(json) ?? json;

            await File.WriteAllTextAsync(
                Path.Combine(TakvimPaths.DataRoot, "arayuz-tani.log"),
                DateTimeOffset.Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                    + Environment.NewLine + text);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
                                      or System.Text.Json.JsonException)
        {
            // Tanı yazılamazsa uygulama yine de açılmalı.
        }
    }

    private static void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;

        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is not ("http" or "https")) return;

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true,
        });
    }
}
