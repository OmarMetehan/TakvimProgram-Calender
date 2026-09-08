using NodaTime;

namespace Takvim.Core.Notifications;

/// <summary>
/// Bildirimlerin susturulduğu saat aralığı.
/// <para>
/// Aralık gece yarısını aşabilir: 22:00–08:00 en sık kurulan penceredir ve iki
/// ayrı güne yayılır. Bu yüzden karşılaştırma "başlangıç &lt; bitiş" varsayımıyla
/// yapılamaz; sarmalanan pencerede koşul <b>veya</b>'ya döner.
/// </para>
/// <para>
/// Başlangıç dahil, bitiş hariçtir: 22:00–08:00 penceresinde saat tam 08:00
/// olduğunda sessizlik biter ve o an bekleyen hatırlatıcı çalar. Tersi
/// seçilseydi bir dakikalık kör nokta kalırdı.
/// </para>
/// </summary>
/// <param name="Enabled">Sessiz saatler açık mı.</param>
/// <param name="Start">Sessizliğin başladığı yerel saat.</param>
/// <param name="End">Sessizliğin bittiği yerel saat.</param>
/// <param name="AllDayOnDaysOff">
/// Çalışılmayan günlerde (hafta sonu, resmi tatil, izin) bütün gün sessiz kalınsın mı.
/// </param>
public readonly record struct QuietHours(
    bool Enabled,
    LocalTime Start,
    LocalTime End,
    bool AllDayOnDaysOff)
{
    /// <summary>Kapalı, 22:00–08:00 kurulu varsayılan.</summary>
    public static QuietHours Default => new(false, new LocalTime(22, 0), new LocalTime(8, 0), false);

    /// <summary>
    /// Başlangıç ve bitiş aynıysa pencere boştur.
    /// <para>
    /// "Sıfır uzunluk" ile "bütün gün" arasında seçim yapmak gerekir; boş kabul
    /// edilir, çünkü yanlışlıkla aynı saati seçen kullanıcının bildirimlerinin
    /// tümden susması sessiz bir veri kaybıdır. Arayüz bu değeri zaten kaydettirmez.
    /// </para>
    /// </summary>
    public bool IsEmptyWindow => Start == End;

    /// <summary>Verilen yerel an sessiz saatlerin içinde mi.</summary>
    /// <param name="moment">Kullanıcının kendi zaman dilimindeki an.</param>
    /// <param name="isDayOff">O gün çalışılmıyor mu (hafta sonu, resmi tatil, izin).</param>
    public bool Covers(LocalDateTime moment, bool isDayOff = false)
    {
        if (!Enabled) return false;
        if (AllDayOnDaysOff && isDayOff) return true;
        if (IsEmptyWindow) return false;

        var time = moment.TimeOfDay;

        return Start < End
            ? time >= Start && time < End          // 13:00–14:00 gibi gün içi pencere
            : time >= Start || time < End;         // 22:00–08:00 gibi gece yarısını aşan pencere
    }

    /// <summary>
    /// Sessizliğin biteceği an; verilen an sessiz saatlerde değilse null.
    /// Arayüzde "08:00'e kadar sessiz" yazısını üretir.
    /// </summary>
    /// <remarks>
    /// İki kural aynı anda susturabilir: saat penceresi ve çalışılmayan gün.
    /// Böyle bir günde geç saatte ikisi de geçerlidir; sessizlik <b>geç bitenle</b>
    /// biter, yoksa arayüz gece yarısı "sessizlik bitti" der ama hatırlatıcı
    /// yine çalmaz.
    /// </remarks>
    public LocalDateTime? EndsAfter(LocalDateTime moment, bool isDayOff = false)
    {
        if (!Covers(moment, isDayOff)) return null;

        LocalDateTime? windowEnd = null;

        if (Covers(moment))
        {
            // Pencerenin bitişi bugüne düşüyorsa bugün, aşıldıysa yarın.
            var todayEnd = moment.Date.At(End);
            windowEnd = todayEnd > moment ? todayEnd : moment.Date.PlusDays(1).At(End);
        }

        // Çalışılmayan gün kuralı devredeyse sessizlik en azından gün sonuna sürer.
        LocalDateTime? dayOffEnd = AllDayOnDaysOff && isDayOff
            ? moment.Date.PlusDays(1).AtMidnight()
            : null;

        return (windowEnd, dayOffEnd) switch
        {
            ({ } window, { } dayOff) => window > dayOff ? window : dayOff,
            ({ } window, null) => window,
            (null, { } dayOff) => dayOff,
            _ => null,
        };
    }
}
