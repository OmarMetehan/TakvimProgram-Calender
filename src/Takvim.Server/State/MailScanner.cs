using Takvim.Data.Services;
using Takvim.Server.Mail;

namespace Takvim.Server.State;

/// <summary>
/// Bağlı posta kutularını arka planda tarar.
/// <para>
/// Tarama sıklığı ayar dosyasından okunur ve sıfır verilirse hiç taranmaz:
/// posta kutusuna ne sıklıkla bakıldığı kullanıcının kararıdır, uygulamanın
/// değil.
/// </para>
/// </summary>
public sealed partial class MailScanner(
    IServiceScopeFactory scopeFactory,
    ILogger<MailScanner> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Posta taraması başarısız oldu.")]
    private partial void LogScanFailed(Exception exception);

    /// <summary>Yoklama sıklığı. Asıl karar ayar dosyasındaki aralıktır; bu yalnızca uyanma ritmi.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>Açılışta hemen ağa çıkılmaz; uygulama önce açılsın.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);

    private DateTimeOffset _lastScan = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval);

        do
        {
            await ScanAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await SafeWaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        try
        {
            var options = MailOAuthOptions.Load();

            // Sıfır aralık "yalnızca elle tara" demektir.
            if (options.ScanIntervalMinutes <= 0 || !options.AnyConfigured) return;

            var due = DateTimeOffset.UtcNow - _lastScan
                   >= TimeSpan.FromMinutes(options.ScanIntervalMinutes);

            if (!due) return;

            await using var scope = scopeFactory.CreateAsyncScope();
            var provider = scope.ServiceProvider;

            var accounts = await provider.GetRequiredService<ProposalService>()
                .GetScannableAsync(ct).ConfigureAwait(false);

            if (accounts.Count == 0)
            {
                _lastScan = DateTimeOffset.UtcNow;
                return;
            }

            var mail = provider.GetRequiredService<MailService>();
            var zones = provider.GetRequiredService<Takvim.Core.Time.TimeZoneService>();

            foreach (var account in accounts)
            {
                if (ct.IsCancellationRequested) break;

                // Her kutu kendi sahibinin zaman diliminde okunur: "yarın 14:00"
                // ifadesi okuyanın diliminde anlamlıdır.
                var zoneId = await provider.GetRequiredService<TakvimDbLookup>()
                    .ZoneOfAsync(account.UserId, ct).ConfigureAwait(false);

                await mail.ScanAsync(account, zones.IsKnown(zoneId) ? zoneId : Takvim.Core.Time.TimeZoneService.DefaultZoneId, ct)
                    .ConfigureAwait(false);
            }

            _lastScan = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException)
        {
            // Uygulama kapanıyor.
        }
#pragma warning disable CA1031 // Arka plan görevi sınırı: hiçbir posta hatası uygulamayı düşürmemeli.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogScanFailed(ex);
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
