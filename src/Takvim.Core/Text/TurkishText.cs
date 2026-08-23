using System.Globalization;
using System.Text;

namespace Takvim.Core.Text;

/// <summary>
/// Türkçe metin normalleştirme. Arama ve sıralamanın tek kaynağıdır.
/// <para>
/// İki ayrı sorunu çözer:
/// <list type="number">
/// <item><b>Noktalı i sorunu.</b> Türkçe'de "I" harfinin küçüğü "ı", "İ" harfinin
/// küçüğü "i"dir. Değişmez kültürle küçültme "İSTANBUL" -> "i̇stanbul" gibi bozuk
/// sonuçlar üretir; bu yüzden Türkçe kültürüyle küçültülür.</item>
/// <item><b>Aksan katlama.</b> Kullanıcı "calisma" yazdığında "çalışma" bulunmalıdır.
/// Türkçe'ye özgü harfler ASCII karşılıklarına indirgenir.</item>
/// </list>
/// </para>
/// </summary>
public static class TurkishText
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    /// <summary>Arama karşılaştırmaları için kullanılacak biçime indirger.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var lowered = value.ToLower(Turkish);
        var builder = new StringBuilder(lowered.Length);
        var lastWasSpace = false;

        foreach (var ch in lowered)
        {
            var mapped = Fold(ch);

            if (char.IsWhiteSpace(mapped))
            {
                // Ardışık boşluklar teke iner; aranan ifadeyle boşluk sayısı eşleşmek zorunda kalmaz.
                if (!lastWasSpace && builder.Length > 0) builder.Append(' ');
                lastWasSpace = true;
                continue;
            }

            lastWasSpace = false;
            builder.Append(mapped);
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>Etkinliğin aranabilir alanlarını tek bir normalleştirilmiş metinde birleştirir.</summary>
    public static string BuildSearchText(params string?[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            var normalized = Normalize(StripHtml(part));
            if (normalized.Length == 0) continue;

            if (builder.Length > 0) builder.Append(' ');
            builder.Append(normalized);
        }

        // Sütun 4000 karakterle sınırlıdır; uzun açıklamalar kırpılır.
        return builder.Length > 4000 ? builder.ToString(0, 4000) : builder.ToString();
    }

    /// <summary>Normalleştirilmiş metnin aranan ifadeyi içerip içermediği.</summary>
    public static bool Contains(string? haystack, string? needle)
    {
        var term = Normalize(needle);
        if (term.Length == 0) return true;
        return Normalize(haystack).Contains(term, StringComparison.Ordinal);
    }

    /// <summary>Türkçe alfabetik sıralama için karşılaştırıcı. "ç" harfi "c"den sonra gelir.</summary>
    public static StringComparer Comparer { get; } = StringComparer.Create(Turkish, ignoreCase: true);

    /// <summary>Zengin metin açıklamadan etiketleri ayıklar; arama metnine HTML karışmaz.</summary>
    public static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;
        if (!html.Contains('<', StringComparison.Ordinal)) return html;

        var builder = new StringBuilder(html.Length);
        var insideTag = false;

        foreach (var ch in html)
        {
            if (ch == '<') { insideTag = true; continue; }
            if (ch == '>') { insideTag = false; builder.Append(' '); continue; }
            if (!insideTag) builder.Append(ch);
        }

        return builder.ToString();
    }

    private static char Fold(char ch) => ch switch
    {
        'ı' or 'î' => 'i',
        'ğ' => 'g',
        'ü' or 'û' => 'u',
        'ş' => 's',
        'ö' => 'o',
        'ç' => 'c',
        'â' => 'a',
        _ => ch,
    };
}
