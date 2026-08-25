using Takvim.Core.Time;
using Takvim.Data.Services;

namespace Takvim.Server.State;

/// <summary>
/// Günde bir kez yedek alır ve eskileri temizler.
/// <para>
/// Uygulama açık olduğu sürece çalışır. Kullanıcının yedek almayı hatırlamasını
/// beklemek işe yaramaz: yedek, ancak ihtiyaç duyulduğunda varlığı fark edilen
/// bir şeydir ve o an geç olur.
/// </para>
/// </summary>
public sealed partial class BackupScheduler(
    IServiceScopeFactory scopeFactory,
    TimeZoneService zones,
    NodaTime.IClock clock,
    ILogger<BackupScheduler> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Günlük yedek alındı: {Name}")]
    private partial void LogCreated(string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Yedek alınamadı: {Reason}")]
    private partial void LogFailed(string reason);

    /// <summary>Yoklama sıklığı. Günlük bir iş için saatte bir bakmak yeterli.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>
    /// Açılışta hemen yedek alınmaz. Uygulama açılır açılmaz diske yazmak,
    /// en yavaş görünen anı daha da yavaşlatır.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

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
            await RunAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await SafeWaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var backups = scope.ServiceProvider.GetRequiredService<BackupService>();

            var today = zones.ToLocal(clock.GetCurrentInstant(), TimeZoneService.DefaultZoneId).Date;

            // Bugün alınmışsa yeniden alınmaz; uygulama gün içinde birkaç kez
            // açılıp kapanabilir.
            if (backups.HasBackupOn(today)) return;

            var result = await backups.CreateAsync(ct: ct).ConfigureAwait(false);

            if (result.Success) LogCreated(result.Backup!.Name);
            else LogFailed(result.Error!);

            backups.Prune();
        }
        catch (OperationCanceledException)
        {
            // Uygulama kapanıyor.
        }
#pragma warning disable CA1031 // Arka plan görevi sınırı: yedek hatası uygulamayı düşürmemeli.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailed(ex.Message);
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
