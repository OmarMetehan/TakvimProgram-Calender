using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Ics;
using Takvim.Core.Localization;
using Takvim.Core.Recurrence;
using Takvim.Core.Time;
using Takvim.Data;
using Takvim.Data.Services;
using Takvim.Server.CalDav;
using Takvim.Server.Components;
using Takvim.Server.Mail;
using Takvim.Server.State;

namespace Takvim.Server;

/// <summary>
/// Uygulamanın kurulumu. Hem masaüstü kabuğu (gömülü olarak) hem de
/// <c>dotnet run</c> ile tarayıcıda tek başına aynı yoldan başlar; böylece
/// geliştirme sırasında görülen davranış ile kullanıcının gördüğü aynı olur.
/// </summary>
public static class TakvimHost
{
    /// <summary>Uygulamayı kurar ama başlatmaz.</summary>
    /// <param name="args">Komut satırı bağımsız değişkenleri.</param>
    /// <param name="urls">Dinlenecek adresler. Masaüstü kabuğu rastgele bir yerel port verir.</param>
    /// <param name="useStaticAssetManifest">
    /// Blazor'ın statik varlık bildirimi kullanılsın mı. Web olarak
    /// çalıştırıldığında evet: parmak izli adresleri ve önceden sıkıştırılmış
    /// kopyaları o yönetir. Masaüstü kabuğunda hayır — kabuk kendi wwwroot
    /// klasörünü kurar ve sıkıştırılmış kopyalar orada yoktur; bildirim yine de
    /// onları sunmaya çalışıp <b>boş yanıt</b> döndürür.
    /// </param>
    public static WebApplication Build(
        string[]? args = null, string? urls = null, bool useStaticAssetManifest = true)
    {
        // İçerik kökü açıkça uygulamanın kendi klasörüdür. Varsayılan çalışma
        // dizinidir; masaüstü kabuğuna çift tıklandığında çalışma dizini
        // kullanıcının bulunduğu yer olur ve ne appsettings.json ne de statik
        // varlık bildirimi bulunabilir. Bildirim bulunamayınca sayfa biçimsiz
        // açılır: stil dosyasının adresi üretilemez.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args ?? [],
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });

        // CalDAV ayarları veritabanından önce okunur: hangi adresin dinleneceği
        // uygulama açılmadan bilinmek zorunda.
        var calDav = CalDavOptions.Load();

        // Arayüz adresi: masaüstü kabuğu verirse o, yoksa yapılandırmadan gelen
        // (launchSettings ya da ASPNETCORE_URLS). CalDAV açıksa ikinci bir
        // dinleyici eklenir — arayüzünkinin yerine geçmez.
        var addresses = new List<string>();

        var uiAddress = !string.IsNullOrWhiteSpace(urls)
            ? urls
            : builder.Configuration["urls"] ?? builder.Configuration["ASPNETCORE_URLS"];

        if (!string.IsNullOrWhiteSpace(uiAddress))
        {
            addresses.AddRange(uiAddress.Split(';', StringSplitOptions.RemoveEmptyEntries));
        }

        if (calDav.Enabled && !addresses.Contains(calDav.ListenUrl, StringComparer.OrdinalIgnoreCase))
        {
            addresses.Add(calDav.ListenUrl);
        }

        if (addresses.Count > 0) builder.WebHost.UseUrls([.. addresses]);

        builder.Services.AddSingleton(calDav);

        TakvimPaths.EnsureCreated();

        // Bekleyen geri yükleme, veritabanına ilk dokunuştan önce uygulanır.
        // Sonraya kalırsa açık bağlantıların altından dosya değişir.
        BackupService.ApplyPendingRestore();

        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        builder.Services.AddDbContext<TakvimDbContext>(options =>
        {
            options.UseSqlite(TakvimPaths.ConnectionString);

            // Geliştirmede sorgu parametreleri günlüğe yazılır; üretimde
            // kişisel veri sızdırmamak için kapalı kalır.
            if (builder.Environment.IsDevelopment()) options.EnableSensitiveDataLogging();
        });

        // Durumsuz çekirdek servisler tek örnek olarak paylaşılır.
        builder.Services.AddSingleton<IClock>(SystemClock.Instance);
        builder.Services.AddSingleton<TimeZoneService>();
        // Tekrarlama motoru tatil takvimini de alır: "tatile denk gelen örneği
        // atla" kuralı ancak tatilleri bilerek uygulanabilir.
        builder.Services.AddSingleton<RecurrenceExpander>();
        builder.Services.AddSingleton<IcsSerializer>();
        builder.Services.AddSingleton<TurkishHolidays>();

        // Veritabanına dokunanlar istek/devre kapsamındadır.
        builder.Services.AddScoped<EventService>();
        builder.Services.AddScoped<CalendarQueryService>();
        builder.Services.AddScoped<UndoService>();
        builder.Services.AddScoped<ReminderService>();
        builder.Services.AddScoped<WorkScheduleService>();
        builder.Services.AddScoped<SchedulingService>();
        builder.Services.AddScoped<CalendarPermissions>();
        builder.Services.AddScoped<AttendeeService>();
        builder.Services.AddScoped<UserDirectory>();
        builder.Services.AddScoped<SharingService>();
        builder.Services.AddScoped<AppPasswordService>();
        builder.Services.AddScoped<CalDavStore>();
        builder.Services.AddScoped<AttachmentService>();
        builder.Services.AddScoped<CalendarService>();
        builder.Services.AddScoped<CategoryService>();
        builder.Services.AddScoped<BackupService>();
        builder.Services.AddScoped<AuditService>();
        builder.Services.AddScoped<NotificationSettingsService>();
        builder.Services.AddScoped<LocationService>();
        builder.Services.AddScoped<TemplateService>();
        builder.Services.AddScoped<SearchService>();
        builder.Services.AddScoped<TaskService>();
        builder.Services.AddScoped<ResourceService>();
        builder.Services.AddScoped<AppointmentService>();

        // Abonelik beslemelerini çeken tek istemci. Yönlendirme izlenir ama
        // kimlik bilgisi taşınmaz: beslemeler herkese açık adreslerdir.
        builder.Services.AddHttpClient<SubscriptionService>(client =>
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Takvim/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("text/calendar, text/plain;q=0.8");
        });

        builder.Services.AddHostedService<SubscriptionRefresher>();

        // Posta kutusu bağlama ve tarama.
        builder.Services.AddScoped<ProposalService>();
        builder.Services.AddScoped<TakvimDbLookup>();
        builder.Services.AddSingleton<LoopbackAuthorizer>();
        builder.Services.AddHttpClient<MailTokenClient>();
        builder.Services.AddHttpClient<MailReader>();
        builder.Services.AddHttpClient<MailService>();
        builder.Services.AddHostedService<MailScanner>();
        builder.Services.AddHostedService<BackupScheduler>();
        builder.Services.AddScoped<CalendarBootstrapper>();

        // Hatırlatıcı zamanlayıcısı uygulama ömrü boyunca tek örnektir; sonucu
        // yayın noktası üzerinden açık devrelere dağıtılır.
        builder.Services.AddSingleton<ReminderBroadcast>();
        builder.Services.AddSingleton<ActiveUserAccessor>();
        builder.Services.AddHostedService<ReminderScheduler>();

        // Blazor devresi başına arayüz durumu.
        builder.Services.AddScoped<CalendarUiState>();

        var app = builder.Build();

        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/hata", createScopeForErrors: true);
        }

        // Sıra önemli: önce port yalıtımı (CalDAV portundan arayüz görünmesin),
        // sonra kimlik doğrulama, sonra geri kalan her şey.
        app.UseMiddleware<CalDavPortIsolation>();
        app.UseMiddleware<CalDavAuthentication>();

        // Durum kodu sayfaları yalnızca arayüz için. CalDAV yollarında devreye
        // girerse 412 gibi anlamlı yanıtlar "/bulunamadi" sayfasında yeniden
        // çalıştırılır ve o sayfa PUT kabul etmediği için 405'e dönüşür.
        app.UseWhen(
            context => !context.Request.Path.StartsWithSegments("/dav"),
            branch => branch.UseStatusCodePagesWithReExecute(
                "/bulunamadi", createScopeForStatusCodePages: true));

        // Dosya sunumu yönlendirmeden önce gelir. Sonraya kalırsa bir uç nokta
        // seçilmiş olur ve StaticFileMiddleware bu durumda kendini devre dışı
        // bırakır; istek, dosyayı sunamayacak bir uç noktaya gider.
        app.UseStaticFiles();

        app.UseAntiforgery();

        if (useStaticAssetManifest)
        {
            // Bildirim giriş derlemesinin adına göre aranır; masaüstü kabuğunda
            // giriş derlemesi Takvim.exe olduğu için adı açıkça verilir.
            app.MapStaticAssets(StaticAssetsManifest);
        }
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
        app.MapCalDav();

        return app;
    }

    /// <summary>
    /// Stil ve betik adreslerine eklenen sürüm damgası.
    /// <para>
    /// Masaüstü kabuğunda varlık adresleri parmak izsiz kalıyor: bildirim
    /// yalnızca web projesinin kendi çıktısında bulunabiliyor. Parmak izi
    /// olmayınca gömülü tarayıcı eski bir cevabı süresiz saklayabiliyor —
    /// nitekim bir kez boş bir yanıtı saklayıp uygulamayı biçimsiz açtı.
    /// Modül kimliği her derlemede değiştiği için damga olarak yeterli.
    /// </para>
    /// </summary>
    public static string AssetVersion { get; } =
        typeof(TakvimHost).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..8];

    /// <summary>
    /// Blazor varlıklarını tanımlayan bildirim dosyasının yolu.
    /// <para>
    /// Ad, giriş derlemesine göre aranır; masaüstü kabuğundan başlatıldığında
    /// giriş derlemesi Takvim.exe olduğu için bulunamaz, bu yüzden açıkça
    /// verilir. Yol da mutlaktır: göreli bir ad çalışma dizinine göre
    /// çözülürdü ve o dizin kullanıcının nereden başlattığına bağlıdır.
    /// </para>
    /// </summary>
    private static string StaticAssetsManifest => Path.Combine(
        AppContext.BaseDirectory,
        typeof(TakvimHost).Assembly.GetName().Name + ".staticwebassets.endpoints.json");

    /// <summary>Şemayı günceller ve ilk açılışta örnek verileri kurar.</summary>
    public static async Task InitializeDatabaseAsync(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TakvimDbContext>();

        await db.Database.MigrateAsync().ConfigureAwait(false);

        var bootstrapper = scope.ServiceProvider.GetRequiredService<CalendarBootstrapper>();
        await bootstrapper.EnsureSeedDataAsync().ConfigureAwait(false);
    }
}
