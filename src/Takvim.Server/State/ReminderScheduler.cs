using Takvim.Data.Services;

namespace Takvim.Server.State;

/// <summary>
/// Zamanı gelen hatırlatıcıları arayüze duyuran yayın noktası.
/// Zamanlayıcı tek örnektir, arayüz durumu ise devre başınadır; ikisi bu
/// olay üzerinden konuşur.
/// </summary>
public sealed class ReminderBroadcast
{
    public event Action<IReadOnlyList<DueReminder>>? Fired;

    public void Publish(IReadOnlyList<DueReminder> reminders)
    {
        if (reminders.Count > 0) Fired?.Invoke(reminders);
    }
}

/// <summary>
/// Hatırlatıcıları düzenli aralıklarla yoklar.
/// <para>
/// Faz 1'de yalnızca uygulama açıkken çalışır: bildirim, arayüzdeki şerittir.
/// Uygulama kapalıyken hatırlatma (sistem tepsisi, Windows bildirimi) masaüstü
/// katmanının işidir ve Faz 1 kapsamına alınmamıştır.
/// </para>
/// </summary>
public sealed partial class ReminderScheduler(
    IServiceScopeFactory scopeFactory,
    ReminderBroadcast broadcast,
    ILogger<ReminderScheduler> logger) : BackgroundService
{
    /// <summary>
    /// Kaynak üreticili günlük kaydı: her çağrıda dize biçimlendirme yapılmaz.
    /// Yoklama yarım dakikada bir çalıştığı için ucuz olması önemlidir.
    /// </summary>
    [LoggerMessage(Level = LogLevel.Error, Message = "Hatırlatıcı yoklaması başarısız oldu.")]
    private partial void LogCheckFailed(Exception exception);

    /// <summary>Yoklama sıklığı. Dakikalık hassasiyet için yarım dakika yeterlidir.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        // İlk tur hemen çalışır ki açılışta yaklaşan etkinlik varsa görülsün.
        do
        {
            await CheckAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await SafeWaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var reminders = scope.ServiceProvider.GetRequiredService<ReminderService>();

            var due = await reminders.GetDueAsync(ct).ConfigureAwait(false);
            broadcast.Publish(due);
        }
        catch (OperationCanceledException)
        {
            // Uygulama kapanıyor.
        }
#pragma warning disable CA1031 // Arka plan görevi tek bir hata yüzünden durmamalı.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // Bir tur başarısız olsa bile zamanlayıcı çalışmaya devam eder;
            // aksi hâlde tek bir hata hatırlatıcıları kalıcı olarak susturur.
            LogCheckFailed(ex);
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
