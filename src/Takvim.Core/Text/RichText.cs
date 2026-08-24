using System.Text;

namespace Takvim.Core.Text;

/// <summary>
/// Açıklama ve gündem alanlarının biçimlendirilmesi.
/// <para>
/// Kullanıcı <b>HTML yazmaz</b>; Markdown'ın küçük bir alt kümesini yazar ve HTML
/// buradan üretilir. Bunun nedeni güvenliktir: gelen HTML'i temizlemek (sanitize)
/// sürekli yeni kaçış yolları çıkan bir uğraştır, oysa çıktıyı biz üretirsek
/// kullanıcı metni her zaman kaçışlanmış olarak geçer ve yalnızca bizim
/// bildiğimiz etiketler oluşur. Ayrıca kaynak düz metin kaldığı için ICS ve
/// CalDAV'a olduğu gibi gider.
/// </para>
/// <para>Desteklenen biçimler:</para>
/// <list type="bullet">
/// <item><c>**kalın**</c>, <c>*eğik*</c>, <c>~~üstü çizili~~</c>, <c>`kod`</c></item>
/// <item><c>- madde</c> ve <c>1. madde</c> listeleri</item>
/// <item><c>#</c>, <c>##</c>, <c>###</c> başlıkları</item>
/// <item><c>&gt; alıntı</c></item>
/// <item>Kendiliğinden bağlanan <c>http(s)://</c> adresleri</item>
/// </list>
/// </summary>
public static class RichText
{
    /// <summary>Biçimlendirme işaretlerinden arındırılmış hâli. Önizleme ve arama bunu kullanır.</summary>
    public static string ToPlainText(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;

        var builder = new StringBuilder(source.Length);

        foreach (var rawLine in Lines(source))
        {
            var line = rawLine.TrimEnd();

            // Satır başındaki işaretler (madde imi, başlık, alıntı) atılır.
            var (kind, content) = ClassifyLine(line);
            if (kind == LineKind.Bullet) content = "• " + content;
            if (kind == LineKind.Numbered) content = "• " + content;

            builder.AppendLine(StripInline(content));
        }

        return builder.ToString().Trim();
    }

    /// <summary>Metni gösterime hazır HTML'e çevirir.</summary>
    public static string ToHtml(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;

        // CalDAV ile gelen açıklamalar HTML içerebilir; onları önce düz metne
        // indiririz, yoksa etiketler kaçışlanıp ekranda görünür.
        if (LooksLikeHtml(source)) source = TurkishText.StripHtml(source);

        var builder = new StringBuilder(source!.Length + 64);
        var openList = LineKind.Text;
        var paragraph = new List<string>();

        void CloseParagraph()
        {
            if (paragraph.Count == 0) return;

            builder.Append("<p>").Append(string.Join("<br>", paragraph)).Append("</p>");
            paragraph.Clear();
        }

        void CloseList()
        {
            if (openList == LineKind.Bullet) builder.Append("</ul>");
            else if (openList == LineKind.Numbered) builder.Append("</ol>");

            openList = LineKind.Text;
        }

        foreach (var rawLine in Lines(source))
        {
            var line = rawLine.TrimEnd();

            if (line.Length == 0)
            {
                CloseParagraph();
                CloseList();
                continue;
            }

            var (kind, content) = ClassifyLine(line);
            var inline = Inline(content);

            switch (kind)
            {
                case LineKind.Bullet:
                case LineKind.Numbered:
                    CloseParagraph();

                    if (openList != kind)
                    {
                        CloseList();
                        builder.Append(kind == LineKind.Bullet ? "<ul>" : "<ol>");
                        openList = kind;
                    }

                    builder.Append("<li>").Append(inline).Append("</li>");
                    break;

                case LineKind.Heading1:
                case LineKind.Heading2:
                case LineKind.Heading3:
                    CloseParagraph();
                    CloseList();

                    // h1 kullanılmaz: bu metin sayfanın içine gömülür, başlık
                    // düzeyleri sayfanın kendi başlığının altında kalmalı.
                    var tag = kind switch
                    {
                        LineKind.Heading1 => "h3",
                        LineKind.Heading2 => "h4",
                        _ => "h5",
                    };

                    builder.Append('<').Append(tag).Append('>')
                           .Append(inline)
                           .Append("</").Append(tag).Append('>');
                    break;

                case LineKind.Quote:
                    CloseParagraph();
                    CloseList();
                    builder.Append("<blockquote>").Append(inline).Append("</blockquote>");
                    break;

                default:
                    CloseList();
                    paragraph.Add(inline);
                    break;
            }
        }

        CloseParagraph();
        CloseList();

        return builder.ToString();
    }

    /// <summary>Metin biçimlendirme işareti taşıyor mu? Araç çubuğunu göstermeye karar verirken kullanılır.</summary>
    public static bool HasFormatting(string? source)
        => !string.IsNullOrWhiteSpace(source)
           && (source.Contains("**", StringComparison.Ordinal)
               || source.Contains("- ", StringComparison.Ordinal)
               || source.Contains('#', StringComparison.Ordinal));

    // ==================================================================

    private enum LineKind { Text, Bullet, Numbered, Heading1, Heading2, Heading3, Quote }

    private static IEnumerable<string> Lines(string source)
        => source.Replace("\r\n", "\n", StringComparison.Ordinal)
                 .Replace('\r', '\n')
                 .Split('\n');

    private static (LineKind Kind, string Content) ClassifyLine(string line)
    {
        var trimmed = line.TrimStart();

        if (trimmed.StartsWith("### ", StringComparison.Ordinal)) return (LineKind.Heading3, trimmed[4..]);
        if (trimmed.StartsWith("## ", StringComparison.Ordinal)) return (LineKind.Heading2, trimmed[3..]);
        if (trimmed.StartsWith("# ", StringComparison.Ordinal)) return (LineKind.Heading1, trimmed[2..]);
        if (trimmed.StartsWith("> ", StringComparison.Ordinal)) return (LineKind.Quote, trimmed[2..]);

        if (trimmed.StartsWith("- ", StringComparison.Ordinal)
            || trimmed.StartsWith("* ", StringComparison.Ordinal)
            || trimmed.StartsWith("• ", StringComparison.Ordinal))
        {
            return (LineKind.Bullet, trimmed[2..]);
        }

        // "1. ", "12) " gibi numaralı madde başları.
        var digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits])) digits++;

        if (digits is > 0 and <= 3
            && digits + 1 < trimmed.Length
            && (trimmed[digits] == '.' || trimmed[digits] == ')')
            && trimmed[digits + 1] == ' ')
        {
            return (LineKind.Numbered, trimmed[(digits + 2)..]);
        }

        return (LineKind.Text, line);
    }

    /// <summary>Satır içi biçimleri uygular. Metin <b>önce</b> kaçışlanır.</summary>
    private static string Inline(string content)
    {
        var escaped = Escape(content);

        // Sıra önemli: "**" tek "*"tan önce, yoksa kalın işaretinin ilk yıldızı
        // eğik olarak yenir.
        escaped = Wrap(escaped, "**", "<strong>", "</strong>");
        escaped = Wrap(escaped, "~~", "<del>", "</del>");
        escaped = Wrap(escaped, "`", "<code>", "</code>");
        escaped = Wrap(escaped, "*", "<em>", "</em>");

        return Linkify(escaped);
    }

    private static string StripInline(string content)
    {
        var result = content
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("~~", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal);

        return result.Replace("*", string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// Bir işaretin çiftlerini etiketle sarar. Tek kalan işaret olduğu gibi
    /// bırakılır — "5 * 3" yazan biri eğik yazı istememiştir.
    /// </summary>
    private static string Wrap(string source, string marker, string open, string close)
    {
        if (!source.Contains(marker, StringComparison.Ordinal)) return source;

        var builder = new StringBuilder(source.Length);
        var index = 0;
        var isOpen = false;

        while (index < source.Length)
        {
            var found = source.IndexOf(marker, index, StringComparison.Ordinal);

            if (found < 0)
            {
                builder.Append(source, index, source.Length - index);
                break;
            }

            // Kapanışı olmayan bir açılış işareti metinde kalır.
            if (!isOpen && source.IndexOf(marker, found + marker.Length, StringComparison.Ordinal) < 0)
            {
                builder.Append(source, index, source.Length - index);
                break;
            }

            builder.Append(source, index, found - index);
            builder.Append(isOpen ? close : open);

            isOpen = !isOpen;
            index = found + marker.Length;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Düz adresleri bağlantıya çevirir. Metin bu noktada kaçışlanmış olduğu için
    /// adresin içinde tırnak ya da köşeli parantez bulunamaz.
    /// </summary>
    private static string Linkify(string escaped)
    {
        // Ortak ön ek "http"tir; şemanın "://" mi "s://" mi olduğu sonra bakılır.
        if (!escaped.Contains("http", StringComparison.OrdinalIgnoreCase)) return escaped;

        var builder = new StringBuilder(escaped.Length + 48);
        var index = 0;
        var copied = 0;

        while (index < escaped.Length)
        {
            var found = escaped.IndexOf("http", index, StringComparison.OrdinalIgnoreCase);
            if (found < 0) break;

            var rest = escaped.AsSpan(found);
            var schemeLength =
                rest.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 8
                : rest.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 7
                : 0;

            if (schemeLength == 0)
            {
                // "http" geçen ama adres olmayan bir kelime.
                index = found + 4;
                continue;
            }

            // Adres ilk boşlukta biter; sondaki noktalama cümleye aittir.
            var end = found + schemeLength;
            while (end < escaped.Length && !char.IsWhiteSpace(escaped[end]) && !StopsUrl(escaped, end)) end++;
            while (end > found + schemeLength && ".,;:!?)".Contains(escaped[end - 1], StringComparison.Ordinal)) end--;

            if (end <= found + schemeLength)
            {
                index = found + schemeLength;
                continue;
            }

            var url = escaped[found..end];

            builder.Append(escaped, copied, found - copied);
            builder.Append("<a href=\"").Append(url).Append("\" target=\"_blank\" rel=\"noopener noreferrer\">")
                   .Append(url).Append("</a>");

            index = end;
            copied = end;
        }

        builder.Append(escaped, copied, escaped.Length - copied);
        return builder.ToString();
    }

    /// <summary>
    /// Adresin bittiği yer. Kaçışlanmış tırnak ve açılı parantez adresin parçası
    /// olamaz; olsaydı üretilen <c>href</c> niteliğinin içinde kalır ve orada
    /// zararsız olsa bile bağlantı metni saçmalardı.
    /// </summary>
    private static bool StopsUrl(string escaped, int index)
    {
        if (escaped[index] == '<') return true;
        if (escaped[index] != '&') return false;

        var rest = escaped.AsSpan(index);

        return rest.StartsWith("&quot;", StringComparison.Ordinal)
            || rest.StartsWith("&#39;", StringComparison.Ordinal)
            || rest.StartsWith("&lt;", StringComparison.Ordinal)
            || rest.StartsWith("&gt;", StringComparison.Ordinal);
    }

    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var ch in value)
        {
            switch (ch)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '"': builder.Append("&quot;"); break;
                case '\'': builder.Append("&#39;"); break;
                default: builder.Append(ch); break;
            }
        }

        return builder.ToString();
    }

    private static bool LooksLikeHtml(string source)
        => source.Contains("<p>", StringComparison.OrdinalIgnoreCase)
           || source.Contains("<br", StringComparison.OrdinalIgnoreCase)
           || source.Contains("<div", StringComparison.OrdinalIgnoreCase)
           || source.Contains("<html", StringComparison.OrdinalIgnoreCase);
}
