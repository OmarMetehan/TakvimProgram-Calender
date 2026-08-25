using System.Text.RegularExpressions;
using NodaTime;
using Takvim.Core.Localization;

namespace Takvim.Core.Mail;

/// <summary>Bir e-postadan çıkarılan etkinlik adayı.</summary>
public sealed record ExtractedEvent(
    string Title,
    LocalDateTime Start,
    LocalDateTime End,
    bool IsAllDay,
    bool RecognizedSchedule)
{
    public string? LocationText { get; init; }
    public string? OnlineMeetingUrl { get; init; }

    /// <summary>Ayrıştırmanın dayandığı cümle; kullanıcı neye bakıldığını görsün diye.</summary>
    public string? Evidence { get; init; }
}

/// <summary>
/// E-posta metninden etkinlik çıkarır.
/// <para>
/// Tarih ve saati bulan iş <see cref="TurkishEventParser"/>'ın; buradaki katman
/// ona <b>hangi cümleyi</b> vereceğine karar verir. Postanın tamamını ayrıştırıcıya
/// vermek işe yaramaz: imza, alıntılanmış yanıtlar ve altbilgi içinde geçen her
/// sayı yanlış bir tarih üretebilir.
/// </para>
/// <para>
/// Bu yüzden metin önce budanır (alıntı ve imza atılır), sonra tarih işareti
/// taşıyan cümleler aranır. Hiçbir cümlede tarih yoksa konu satırı denenir;
/// o da vermezse öneri yine üretilir ama "saati siz seçin" diye işaretlenir.
/// </para>
/// </summary>
public static partial class MailEventExtractor
{
    /// <summary>Ayrıştırıcıya verilecek en uzun cümle; daha uzunu gürültüdür.</summary>
    private const int MaxSentenceLength = 200;

    /// <summary>Bakılacak en fazla cümle sayısı.</summary>
    private const int MaxSentences = 40;

    /// <summary>
    /// Alıntılanmış yanıtın başladığı yeri gösteren kalıplar. Bunlardan
    /// sonrası okunmaz: orası eski bir postadır ve tarihleri geçmişe aittir.
    /// </summary>
    private static readonly string[] QuoteMarkers =
    [
        "-----Original Message-----",
        "-----Özgün İleti-----",
        "________________________________",
        "Kimden:",
        "Gönderen:",
        "From:",
        "On Mon,", "On Tue,", "On Wed,", "On Thu,", "On Fri,", "On Sat,", "On Sun,",
        "tarihinde şunu yazdı:",
        "wrote:",
    ];

    /// <summary>İmzanın başladığı yeri gösteren kalıplar.</summary>
    private static readonly string[] SignatureMarkers =
    [
        "\n-- \n",
        "\nSaygılarımla",
        "\nİyi çalışmalar",
        "\nBest regards",
        "\nKind regards",
    ];

    /// <summary>Bir cümlede tarih ya da saat olduğunu düşündüren işaretler.</summary>
    private static readonly string[] ScheduleHints =
    [
        "pazartesi", "salı", "çarşamba", "perşembe", "cuma", "cumartesi", "pazar",
        "bugün", "yarın", "öbür gün", "haftaya", "gelecek hafta",
        "ocak", "şubat", "mart", "nisan", "mayıs", "haziran",
        "temmuz", "ağustos", "eylül", "ekim", "kasım", "aralık",
        "saat", "toplantı", "görüşme", "buluşma", "randevu",
    ];

    [GeneratedRegex(@"https?://[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"\b\d{1,2}[:.]\d{2}\b")]
    private static partial Regex ClockPattern();

    /// <summary>Konum satırı: "Yer: ...", "Konum: ...", "Location: ...".</summary>
    [GeneratedRegex(@"^\s*(yer|konum|adres|location|place)\s*[:：]\s*(?<value>.{2,120})$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex LocationPattern();

    // ==================================================================

    /// <summary>
    /// Konu ve gövdeden bir etkinlik adayı çıkarır. Metinde hiçbir etkinlik
    /// izi yoksa null döner — her postadan öneri üretmek, öneri kutusunu
    /// işe yaramaz kılardı.
    /// </summary>
    public static ExtractedEvent? Extract(string? subject, string? body, LocalDateTime now)
    {
        var trimmedBody = Prune(body);
        var sentences = Sentences(trimmedBody);

        // 1) Gövdede tarih işareti taşıyan ilk cümle.
        foreach (var sentence in sentences)
        {
            if (!LooksScheduled(sentence)) continue;

            var parsed = TurkishEventParser.Parse(sentence, now);
            if (!parsed.RecognizedSchedule) continue;

            return Build(parsed, subject, trimmedBody, sentence);
        }

        // 2) Konu satırı. Toplantı davetlerinde tarih sık sık oradadır.
        if (!string.IsNullOrWhiteSpace(subject))
        {
            var fromSubject = TurkishEventParser.Parse(subject, now);

            if (fromSubject.RecognizedSchedule)
            {
                return Build(fromSubject, subject, trimmedBody, subject);
            }
        }

        // 3) Tarih yok ama toplantı sözü var: öneri üretilir, saati kullanıcı seçer.
        if (MentionsMeeting(subject) || sentences.Any(MentionsMeeting))
        {
            var fallback = TurkishEventParser.Parse(subject ?? "Toplantı", now);

            return Build(fallback with { RecognizedSchedule = false }, subject, trimmedBody, null);
        }

        return null;
    }

    /// <summary>Metinden çevrimiçi toplantı bağlantısını çıkarır.</summary>
    public static string? FindMeetingUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        foreach (Match match in UrlPattern().Matches(text))
        {
            var url = match.Value.TrimEnd('.', ',', ')', ';');

            if (Scheduling.MeetingLinks.DetectProvider(url) is not (null or "diger")) return url;
        }

        return null;
    }

    /// <summary>"Yer:" gibi bir satır varsa konumu döner.</summary>
    public static string? FindLocation(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var match = LocationPattern().Match(text);
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    // ==================================================================

    private static ExtractedEvent Build(
        ParsedEvent parsed, string? subject, string body, string? evidence)
    {
        // Başlık için konu satırı yeğlenir: ayrıştırıcının cümleden arta
        // bıraktığı metin çoğu zaman yarım kalır, konu ise zaten bir başlıktır.
        var title = string.IsNullOrWhiteSpace(subject) ? parsed.Title : CleanSubject(subject);

        return new ExtractedEvent(
            string.IsNullOrWhiteSpace(title) ? "Toplantı" : title,
            parsed.Start,
            parsed.End,
            parsed.IsAllDay,
            parsed.RecognizedSchedule)
        {
            LocationText = FindLocation(body),
            OnlineMeetingUrl = FindMeetingUrl(body),
            Evidence = evidence,
        };
    }

    /// <summary>Konu satırındaki "RE:", "FW:" gibi ekleri atar.</summary>
    private static string CleanSubject(string subject)
    {
        var cleaned = subject.Trim();

        while (true)
        {
            var trimmed = cleaned;

            foreach (var prefix in (string[])["re:", "fw:", "fwd:", "yan:", "ilt:"])
            {
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    trimmed = trimmed[prefix.Length..].TrimStart();
                }
            }

            if (trimmed == cleaned) return cleaned;
            cleaned = trimmed;
        }
    }

    /// <summary>Alıntılanmış yanıtı ve imzayı atar.</summary>
    private static string Prune(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;

        var text = body.Replace("\r\n", "\n", StringComparison.Ordinal);
        var cut = text.Length;

        foreach (var marker in QuoteMarkers.Concat(SignatureMarkers))
        {
            var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && index < cut) cut = index;
        }

        return text[..cut].Trim();
    }

    private static List<string> Sentences(string text)
    {
        if (text.Length == 0) return [];

        var parts = text.Split(['\n', '.', '!', '?', ';'], StringSplitOptions.RemoveEmptyEntries);
        var sentences = new List<string>(Math.Min(parts.Length, MaxSentences));

        foreach (var part in parts)
        {
            var trimmed = part.Trim();

            if (trimmed.Length is 0 or > MaxSentenceLength) continue;

            sentences.Add(trimmed);
            if (sentences.Count >= MaxSentences) break;
        }

        return sentences;
    }

    /// <summary>
    /// İşaretlerin aksansız hâlleri. Karşılaştırma normalleştirilmiş metin
    /// üzerinde yapılır: e-postada "toplanti" da "TOPLANTI" da yazılabilir ve
    /// ikisi de aynı şeydir. Aramanın kullandığı katlama kuralı burada da geçerli.
    /// </summary>
    private static readonly string[] NormalizedHints =
        [.. ScheduleHints.Select(Text.TurkishText.Normalize)];

    private static readonly string[] MeetingWords =
        ["toplantı", "görüşme", "randevu", "buluşma", "davet"];

    private static readonly string[] NormalizedMeetingWords =
        [.. MeetingWords.Select(Text.TurkishText.Normalize)];

    private static bool LooksScheduled(string sentence)
    {
        if (ClockPattern().IsMatch(sentence)) return true;

        var normalized = Text.TurkishText.Normalize(sentence);

        return Array.Exists(NormalizedHints, hint => normalized.Contains(hint, StringComparison.Ordinal));
    }

    private static bool MentionsMeeting(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var normalized = Text.TurkishText.Normalize(text);

        return Array.Exists(NormalizedMeetingWords,
            word => normalized.Contains(word, StringComparison.Ordinal));
    }
}
