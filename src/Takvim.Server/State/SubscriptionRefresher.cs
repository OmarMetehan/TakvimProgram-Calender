using Takvim.Data.Services;

namespace Takvim.Server.State;

/// <summary>
/// Abone takvimleri arka planda tazeler.
/// <para>
/// Yoklama sıklığı, beslemelerin kendi tazeleme aralıklarından bağımsızdır:
/// burada yalnızca "zamanı gelen var mı" diye bakılır, hangisinin zamanının
/// geldiğine <see cref="SubscriptionService.RefreshDueAsync"/> karar verir.
/// </para>
/// </summary>
public sealed partial class SubscriptionRefresher(
    IServiceScopeFactory scopeFactory,
    ILogger<SubscriptionRefresher> logger) : BackgroundService
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Abonelik tazelemesi başarısız oldu.")]
    private partial void LogRefreshFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} abonelik tazelendi.")]
    private partial void LogRefreshed(int count);

    /// <summary>Yoklama sıklığı. En kısa tazeleme aralığı 15 dakika olduğu için beşer dakika yeter.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Açılışta hemen ağa çıkılmaz: uygulama önce açılsın, kullanıcı ekranını
    /// görsün. Bağlantısı olmayan bir makinede açılış yavaşlamamalı.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

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
            await RefreshAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await SafeWaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var subscriptions = scope.ServiceProvider.GetRequiredService<SubscriptionService>();

            var count = await subscriptions.RefreshDueAsync(ct).ConfigureAwait(false);
            if (count > 0) LogRefreshed(count);
        }
        catch (OperationCanceledException)
        {
            // Uygulama kapanıyor.
        }
#pragma warning disable CA1031 // Arka plan görevi sınırı: hiçbir besleme hatası uygulamayı düşürmemeli.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRefreshFailed(ex);
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
