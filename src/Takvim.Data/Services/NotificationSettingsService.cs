using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Notifications;
using Takvim.Core.Time;

namespace Takvim.Data.Services;

/// <summary>Bir kullanıcının bildirim tercihleri.</summary>
/// <param name="RemindersEnabled">Hatırlatıcılar hiç çalsın mı.</param>
/// <param name="Quiet">Sessiz saat penceresi.</param>
public sealed record NotificationSettings(bool RemindersEnabled, QuietHours Quiet)
{
    public static NotificationSettings Default => new(true, QuietHours.Default);
}

/// <summary>Bildirimlerin şu an susturulup susturulmadığı ve nedeni.</summary>
/// <param name="IsSilenced">Şu an bildirim gösterilmez mi.</param>
/// <param name="Reason">Arayüzde gösterilecek kısa açıklama; susturulmuşsa dolu.</param>
/// <param name="EndsAt">Sessizliğin biteceği yerel an; süresiz kapalıysa null.</param>
public sealed record SilenceState(bool IsSilenced, string? Reason, LocalDateTime? EndsAt)
{
    public static SilenceState Audible => new(false, null, null);
}

/// <summary>
/// Bildirim tercihlerini okur, yazar ve "şu an sessiz miyiz" sorusunu yanıtlar.
/// <para>
/// Sessizlik hatırlatıcıyı <b>silmez</b>, yalnızca o turda göstermez. Pencere
/// kapandığında hatırlatıcı hâlâ zamanındaysa çalar: 08:30'daki toplantının
/// 07:30'da tetiklenen uyarısı, 08:00'de sessizlik bitince görünür. Çok
/// gerilerde kalanları ise hatırlatıcı motorunun kendi gecikme sınırı
/// (<see cref="ReminderService.MissedGrace"/>) zaten eler; birikmiş bildirim
/// yığını oluşmaz.
/// </para>
/// </summary>
public sealed class NotificationSettingsService(
    TakvimDbContext db,
    WorkScheduleService schedules,
    TimeZoneService zones,
    IClock clock)
{
    /// <summary>Kullanıcının tercihleri. Kullanıcı yoksa varsayılan döner.</summary>
    public async Task<NotificationSettings> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ct).ConfigureAwait(false);

        return user is null
            ? NotificationSettings.Default
            : new NotificationSettings(user.RemindersEnabled, user.QuietHours);
    }

    /// <summary>Tercihleri kaydeder.</summary>
    /// <exception cref="InvalidOperationException">Sessiz saat penceresi sıfır uzunluktaysa.</exception>
    public async Task SaveAsync(Guid userId, NotificationSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Sıfır uzunluklu pencere sessizce hiçbir şey yapmaz; kullanıcı ise
        // sessiz saatleri kurduğunu sanır. Kaydettirilmez.
        if (settings.Quiet.Enabled && settings.Quiet.IsEmptyWindow)
        {
            throw new InvalidOperationException(
                "Sessiz saatlerin başlangıcı ve bitişi aynı olamaz.");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct).ConfigureAwait(false);
        if (user is null) return;

        user.RemindersEnabled = settings.RemindersEnabled;
        user.QuietHoursEnabled = settings.Quiet.Enabled;
        user.QuietHoursStart = settings.Quiet.Start;
        user.QuietHoursEnd = settings.Quiet.End;
        user.QuietOnDaysOff = settings.Quiet.AllDayOnDaysOff;
        user.UpdatedAt = clock.GetCurrentInstant().ToDateTimeOffset();

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Şu an bildirim gösterilebilir mi. Karar kullanıcının kendi zaman
    /// diliminde verilir: sessiz saatler duvar saatidir, UTC değil.
    /// </summary>
    public async Task<SilenceState> EvaluateAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ct).ConfigureAwait(false);

        if (user is null) return SilenceState.Audible;

        if (!user.RemindersEnabled)
        {
            return new SilenceState(true, "Hatırlatıcı bildirimleri kapalı.", null);
        }

        var quiet = user.QuietHours;
        if (!quiet.Enabled) return SilenceState.Audible;

        var zoneId = zones.IsKnown(user.TimeZoneId) ? user.TimeZoneId : TimeZoneService.DefaultZoneId;
        var now = zones.ToLocal(clock.GetCurrentInstant(), zoneId);

        // Çalışılmayan gün kuralı kapalıysa mesai düzenini hiç sorgulamayız:
        // hatırlatıcı yoklaması yarım dakikada bir çalışır, boşuna sorgu açmaz.
        var isDayOff = false;

        if (quiet.AllDayOnDaysOff)
        {
            var day = await schedules.GetDayAsync(userId, now.Date, ct).ConfigureAwait(false);
            isDayOff = !day.IsWorkingDay;
        }

        if (!quiet.Covers(now, isDayOff)) return SilenceState.Audible;

        var reason = quiet.Covers(now)
            ? "Sessiz saatler."
            : "Bugün çalışılmıyor.";

        return new SilenceState(true, reason, quiet.EndsAfter(now, isDayOff));
    }
}
