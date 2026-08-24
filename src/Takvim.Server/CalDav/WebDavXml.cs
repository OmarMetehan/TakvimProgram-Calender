using System.Xml.Linq;

namespace Takvim.Server.CalDav;

/// <summary>
/// WebDAV ve CalDAV XML gövdelerini kurar ve okur.
/// <para>
/// İstemciler ad alanı ön eklerini kendileri seçer (<c>D:</c>, <c>d:</c>,
/// <c>a:</c>…), bu yüzden okuma daima tam ad alanı adına göre yapılır; ön ek
/// hiçbir zaman karşılaştırılmaz.
/// </para>
/// </summary>
public static class WebDavXml
{
    /// <summary>WebDAV çekirdeği.</summary>
    public static readonly XNamespace Dav = "DAV:";

    /// <summary>
    /// CalDAV (RFC 4791). Adı "Cal": bu tür "Takvim.Server.CalDav" ad alanında
    /// bulunuyor ve "CalDav" adı ad alanıyla çakışırdı.
    /// </summary>
    public static readonly XNamespace Cal = "urn:ietf:params:xml:ns:caldav";

    /// <summary>Apple'ın CalendarServer eklentileri; getctag buradan gelir.</summary>
    public static readonly XNamespace CalendarServer = "http://calendarserver.org/ns/";

    /// <summary>Apple iCal eklentileri; takvim rengi buradan gelir.</summary>
    public static readonly XNamespace Apple = "http://apple.com/ns/ical/";

    public const string CalendarContentType = "text/calendar; charset=utf-8";
    public const string XmlContentType = "application/xml; charset=utf-8";

    /// <summary>Bir kaynağın tek bir özelliği ve bulunup bulunmadığı.</summary>
    /// <param name="Name">Özelliğin tam adı.</param>
    /// <param name="Content">Değer; bulunamadıysa null.</param>
    public sealed record Property(XName Name, object? Content)
    {
        public bool Found => Content is not null;
    }

    /// <summary>
    /// Çok durumlu (207) yanıt kurar.
    /// <para>
    /// Bulunan ve bulunamayan özellikler ayrı <c>propstat</c> bloklarına
    /// ayrılır; RFC 4918 bunu şart koşar ve iOS eksik özellikleri 200 içinde
    /// görürse koleksiyonu reddeder.
    /// </para>
    /// </summary>
    public static XDocument MultiStatus(IEnumerable<(string Href, IReadOnlyList<Property> Properties)> responses)
    {
        ArgumentNullException.ThrowIfNull(responses);

        var root = new XElement(Dav + "multistatus",
            new XAttribute(XNamespace.Xmlns + "d", Dav.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "c", Cal.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "cs", CalendarServer.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "ical", Apple.NamespaceName));

        foreach (var (href, properties) in responses)
        {
            root.Add(BuildResponse(href, properties));
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    private static XElement BuildResponse(string href, IReadOnlyList<Property> properties)
    {
        var response = new XElement(Dav + "response", new XElement(Dav + "href", href));

        var found = properties.Where(p => p.Found).ToList();
        var missing = properties.Where(p => !p.Found).ToList();

        if (found.Count > 0)
        {
            response.Add(new XElement(Dav + "propstat",
                new XElement(Dav + "prop", found.Select(p => new XElement(p.Name, p.Content))),
                new XElement(Dav + "status", "HTTP/1.1 200 OK")));
        }

        if (missing.Count > 0)
        {
            response.Add(new XElement(Dav + "propstat",
                new XElement(Dav + "prop", missing.Select(p => new XElement(p.Name))),
                new XElement(Dav + "status", "HTTP/1.1 404 Not Found")));
        }

        return response;
    }

    /// <summary>Silinmiş kaynakları bildiren yanıt; <c>sync-collection</c> bunu kullanır.</summary>
    public static XElement NotFoundResponse(string href)
        => new(Dav + "response",
            new XElement(Dav + "href", href),
            new XElement(Dav + "status", "HTTP/1.1 404 Not Found"));

    // ------------------------------------------------------------------
    // Okuma
    // ------------------------------------------------------------------

    /// <summary>
    /// İstenen özelliklerin adlarını çıkarır.
    /// <c>allprop</c> ya da gövdesiz istekte boş liste döner; çağıran bunu
    /// "varsayılan küme" olarak yorumlar.
    /// </summary>
    public static IReadOnlyList<XName> ReadRequestedProperties(XDocument? request)
    {
        var prop = request?.Root?.Element(Dav + "prop");
        if (prop is null) return [];

        return [.. prop.Elements().Select(e => e.Name)];
    }

    public static bool IsAllProp(XDocument? request)
        => request?.Root?.Element(Dav + "allprop") is not null;

    /// <summary>Gövdeyi okur. Boş ya da bozuk gövde null döner; istisna fırlatılmaz.</summary>
    public static async Task<XDocument?> ReadBodyAsync(Stream body, CancellationToken ct = default)
    {
        using var reader = new StreamReader(body, leaveOpen: true);
        var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            return XDocument.Parse(text);
        }
        catch (System.Xml.XmlException)
        {
            // Bozuk XML gönderen istemci sunucuyu düşürmemeli.
            return null;
        }
    }

    /// <summary>
    /// <c>Depth</c> başlığını okur. Belirtilmemişse koleksiyonlar için
    /// istemcilerin çoğu 0 varsayar; çağıran kendi varsayılanını verir.
    /// </summary>
    public static int ReadDepth(string? header, int fallback)
        => header?.Trim().ToLowerInvariant() switch
        {
            "0" => 0,
            "1" => 1,
            // "infinity" desteklenmiyor; en fazla bir seviye inilir.
            "infinity" => 1,
            _ => fallback,
        };

    /// <summary>Etiketi HTTP başlığında kullanılacak biçime sokar.</summary>
    public static string QuoteETag(string etag) => $"\"{etag}\"";
}
