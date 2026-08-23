using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NodaTime;
using NodaTime.Text;

namespace Takvim.Data.Converters;

/// <summary>
/// NodaTime ve DateTimeOffset tiplerini SQLite'ın anladığı metne çevirir.
/// Biçimler sabit uzunlukludur; böylece SQLite'ın metin karşılaştırması
/// kronolojik sıralamayla birebir örtüşür ve indeksler aralık sorgularında çalışır.
/// </summary>
public static class NodaTimeConverters
{
    internal static readonly LocalDateTimePattern LocalPattern =
        LocalDateTimePattern.CreateWithInvariantCulture("uuuu-MM-ddTHH:mm:ss");

    internal static readonly InstantPattern UtcPattern =
        InstantPattern.CreateWithInvariantCulture("uuuu-MM-ddTHH:mm:ss'Z'");

    /// <summary>
    /// DateTimeOffset, NodaTime değil BCL biçimlendirmesi kullanır: yıl belirteci
    /// "uuuu" değil "yyyy"dir. Yanlış belirteç sessizce harfi harfine yazılır.
    /// </summary>
    internal const string OffsetFormat = "yyyy-MM-ddTHH:mm:ss.fffffff'Z'";

    /// <summary>Yerel tarih-saat: zaman dilimsiz duvar saati. Ayrı bir sütunda IANA kimliğiyle eşleşir.</summary>
    public sealed class LocalDateTimeToStringConverter()
        : ValueConverter<LocalDateTime, string>(
            v => LocalPattern.Format(v),
            v => LocalPattern.Parse(v).Value);

    /// <summary>Mutlak an. Yalnızca türetilmiş sorgu önbelleği sütunlarında kullanılır.</summary>
    public sealed class InstantToStringConverter()
        : ValueConverter<Instant, string>(
            v => UtcPattern.Format(v),
            v => UtcPattern.Parse(v).Value);

    /// <summary>Oluşturma/güncelleme damgaları. Her zaman UTC'ye normalleştirilir.</summary>
    public sealed class DateTimeOffsetToStringConverter()
        : ValueConverter<DateTimeOffset, string>(
            v => v.ToUniversalTime().ToString(OffsetFormat, CultureInfo.InvariantCulture),
            v => DateTimeOffset.Parse(v, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal));
}
