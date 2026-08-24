using System.Globalization;
using System.Xml.Linq;
using NodaTime;
using NodaTime.Text;
using Takvim.Core.Domain;
using Takvim.Data.Services;
using static Takvim.Server.CalDav.WebDavXml;

namespace Takvim.Server.CalDav;

/// <summary>
/// CalDAV (RFC 4791) uç noktaları.
/// <para>
/// Adres yapısı: <c>/dav/</c> keşif, <c>/dav/p/{kullanıcı}/</c> asıl kayıt,
/// <c>/dav/c/{kullanıcı}/</c> takvim listesi, <c>/dav/c/{kullanıcı}/{takvim}/</c>
/// koleksiyon, <c>…/{uid}.ics</c> kaynak.
/// </para>
/// <para>
/// Adresteki kullanıcı kimliği <b>yetki kaynağı değildir</b>. Yetki daima
/// doğrulanmış kullanıcıdan gelir; adres yalnızca hangi kaynağın istendiğini
/// söyler. Başkasının adresini yazmak erişim vermez.
/// </para>
/// </summary>
public static class CalDavEndpoints
{
    private static readonly InstantPattern HttpDate =
        InstantPattern.CreateWithInvariantCulture("ddd, dd MMM uuuu HH:mm:ss 'GMT'");

    /// <summary>CalDAV zaman damgası biçimi: 20260302T090000Z.</summary>
    private static readonly InstantPattern CalDavInstant =
        InstantPattern.CreateWithInvariantCulture("uuuuMMdd'T'HHmmss'Z'");

    public static void MapCalDav(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Keşif: istemci yalnızca sunucu adresini bilir, gerisini buradan bulur.
        app.MapMethods("/.well-known/caldav", ["GET", "PROPFIND", "OPTIONS"],
            (HttpContext context) =>
            {
                context.Response.Headers.Location = "/dav/";
                return Results.StatusCode(StatusCodes.Status301MovedPermanently);
            });

        app.MapMethods("/dav/{**rest}", ["OPTIONS"], HandleOptions);
        app.MapMethods("/dav", ["OPTIONS"], HandleOptions);

        app.MapMethods("/dav/", ["PROPFIND"], DiscoverRootAsync);
        app.MapMethods("/dav/p/{userId}/", ["PROPFIND"], DescribePrincipalAsync);
        app.MapMethods("/dav/c/{userId}/", ["PROPFIND"], ListCalendarsAsync);

        app.MapMethods("/dav/c/{userId}/{calendarId}/", ["PROPFIND"], DescribeCollectionAsync);
        app.MapMethods("/dav/c/{userId}/{calendarId}/", ["REPORT"], ReportAsync);

        app.MapMethods("/dav/c/{userId}/{calendarId}/{resource}", ["GET"], GetResourceAsync);
        app.MapMethods("/dav/c/{userId}/{calendarId}/{resource}", ["PUT"], PutResourceAsync);
        app.MapMethods("/dav/c/{userId}/{calendarId}/{resource}", ["DELETE"], DeleteResourceAsync);
    }

    // ==================================================================
    // OPTIONS — yeteneklerin ilanı
    // ==================================================================

    private static IResult HandleOptions(HttpContext context)
    {
        // İstemci bu başlıklara bakarak sunucunun CalDAV konuşup konuşmadığına karar verir.
        context.Response.Headers["DAV"] = "1, 2, 3, calendar-access, extended-mkcol";
        context.Response.Headers.Allow = "OPTIONS, GET, PUT, DELETE, PROPFIND, REPORT";

        return Results.Ok();
    }

    // ==================================================================
    // PROPFIND — keşif
    // ==================================================================

    /// <summary>Kök: istemciye "sen kimsin" sorusunun cevabını verir.</summary>
    private static MultiStatusDocument DiscoverRootAsync(HttpContext context)
    {
        var user = CalDavAuthentication.RequireUser(context);
        var principal = PrincipalHref(user.Id);

        var properties = new List<Property>
        {
            new(Dav + "resourcetype", new XElement(Dav + "collection")),
            new(Dav + "current-user-principal", new XElement(Dav + "href", principal)),
            new(Dav + "principal-URL", new XElement(Dav + "href", principal)),
        };

        return MultiStatusResult(MultiStatus([("/dav/", properties)]));
    }

    /// <summary>Asıl kayıt: takvimlerin nerede olduğunu söyler.</summary>
    private static MultiStatusDocument DescribePrincipalAsync(HttpContext context)
    {
        var user = CalDavAuthentication.RequireUser(context);

        var properties = new List<Property>
        {
            new(Dav + "resourcetype", new XElement(Dav + "principal")),
            new(Dav + "displayname", user.DisplayName),
            new(Dav + "current-user-principal", new XElement(Dav + "href", PrincipalHref(user.Id))),
            new(Dav + "principal-URL", new XElement(Dav + "href", PrincipalHref(user.Id))),
            new(Cal + "calendar-home-set", new XElement(Dav + "href", HomeHref(user.Id))),
            new(Cal + "calendar-user-address-set", new XElement(Dav + "href", "mailto:" + user.Email)),
        };

        return MultiStatusResult(MultiStatus([(PrincipalHref(user.Id), properties)]));
    }

    /// <summary>Takvim listesi.</summary>
    private static async Task<IResult> ListCalendarsAsync(HttpContext context, CalDavStore store)
    {
        var user = CalDavAuthentication.RequireUser(context);
        var depth = ReadDepth(context.Request.Headers["Depth"], fallback: 1);

        var responses = new List<(string, IReadOnlyList<Property>)>
        {
            (HomeHref(user.Id),
            [
                new Property(Dav + "resourcetype", new XElement(Dav + "collection")),
                new Property(Dav + "displayname", "Takvimler"),
                new Property(Dav + "current-user-principal", new XElement(Dav + "href", PrincipalHref(user.Id))),
            ]),
        };

        if (depth > 0)
        {
            var collections = await store.GetCollectionsAsync(user.Id, context.RequestAborted)
                .ConfigureAwait(false);

            foreach (var collection in collections)
            {
                responses.Add((CollectionHref(user.Id, collection.Calendar.Id),
                    CollectionProperties(collection, user)));
            }
        }

        return MultiStatusResult(MultiStatus(responses));
    }

    /// <summary>Tek bir koleksiyon ve (Depth: 1 ise) içindeki kaynaklar.</summary>
    private static async Task<IResult> DescribeCollectionAsync(
        HttpContext context, CalDavStore store, Guid calendarId)
    {
        var user = CalDavAuthentication.RequireUser(context);

        var collection = await store.GetCollectionAsync(user.Id, calendarId, context.RequestAborted)
            .ConfigureAwait(false);

        if (collection is null) return Results.NotFound();

        var depth = ReadDepth(context.Request.Headers["Depth"], fallback: 0);

        var responses = new List<(string, IReadOnlyList<Property>)>
        {
            (CollectionHref(user.Id, calendarId), CollectionProperties(collection, user)),
        };

        if (depth > 0)
        {
            var resources = await store.ListResourcesAsync(calendarId, context.RequestAborted)
                .ConfigureAwait(false);

            foreach (var resource in resources)
            {
                responses.Add((ResourceHref(user.Id, calendarId, resource.Uid), ResourceProperties(resource)));
            }
        }

        return MultiStatusResult(MultiStatus(responses));
    }

    // ==================================================================
    // REPORT — sorgular
    // ==================================================================

    private static async Task<IResult> ReportAsync(HttpContext context, CalDavStore store, Guid calendarId)
    {
        var user = CalDavAuthentication.RequireUser(context);

        var collection = await store.GetCollectionAsync(user.Id, calendarId, context.RequestAborted)
            .ConfigureAwait(false);

        if (collection is null) return Results.NotFound();

        var body = await ReadBodyAsync(context.Request.Body, context.RequestAborted).ConfigureAwait(false);
        var reportName = body?.Root?.Name;

        if (reportName == Cal + "calendar-query")
            return await CalendarQueryAsync(context, store, user, calendarId, body!).ConfigureAwait(false);

        if (reportName == Cal + "calendar-multiget")
            return await CalendarMultiGetAsync(context, store, user, calendarId, body!).ConfigureAwait(false);

        if (reportName == Dav + "sync-collection")
            return await SyncCollectionAsync(context, store, user, calendarId, body!).ConfigureAwait(false);

        return Results.BadRequest("Desteklenmeyen rapor türü.");
    }

    /// <summary>
    /// <c>calendar-query</c>: bir tarih aralığındaki kaynaklar. İstemci
    /// genellikle içeriği de ister, o zaman ICS gövdesi de eklenir.
    /// </summary>
    private static async Task<IResult> CalendarQueryAsync(
        HttpContext context, CalDavStore store, User user, Guid calendarId, XDocument body)
    {
        var requested = ReadRequestedProperties(body);
        var wantsData = requested.Contains(Cal + "calendar-data");

        var (from, to) = ReadTimeRange(body);

        var resources = from is null || to is null
            ? await store.ListResourcesAsync(calendarId, context.RequestAborted).ConfigureAwait(false)
            : await store.ListResourcesInRangeAsync(calendarId, from.Value, to.Value, context.RequestAborted)
                .ConfigureAwait(false);

        return await BuildResourceResponsesAsync(
            context, store, user, calendarId, resources, wantsData).ConfigureAwait(false);
    }

    /// <summary><c>calendar-multiget</c>: istemcinin adını verdiği kaynaklar.</summary>
    private static async Task<IResult> CalendarMultiGetAsync(
        HttpContext context, CalDavStore store, User user, Guid calendarId, XDocument body)
    {
        var requested = ReadRequestedProperties(body);
        var wantsData = requested.Contains(Cal + "calendar-data");

        var hrefs = body.Root!
            .Elements(Dav + "href")
            .Select(e => e.Value)
            .ToList();

        var all = await store.ListResourcesAsync(calendarId, context.RequestAborted).ConfigureAwait(false);
        var byUid = all.ToDictionary(r => r.Uid, StringComparer.Ordinal);

        var responses = new List<(string, IReadOnlyList<Property>)>();
        var missing = new List<XElement>();

        foreach (var href in hrefs)
        {
            var uid = UidFromHref(href);

            if (uid is null || !byUid.TryGetValue(uid, out var resource))
            {
                missing.Add(NotFoundResponse(href));
                continue;
            }

            var properties = ResourceProperties(resource);

            if (wantsData)
            {
                var content = await store.GetResourceAsync(calendarId, uid, context.RequestAborted)
                    .ConfigureAwait(false);

                if (content is { } found)
                {
                    properties = [.. properties, new Property(Cal + "calendar-data", found.Ics)];
                }
            }

            responses.Add((ResourceHref(user.Id, calendarId, uid), properties));
        }

        var document = MultiStatus(responses);
        foreach (var element in missing) document.Root!.Add(element);

        return MultiStatusResult(document);
    }

    /// <summary>
    /// <c>sync-collection</c>: son imleçten bu yana değişenler. İstemci her
    /// açılışta koleksiyonun tamamını çekmek yerine bunu kullanır.
    /// </summary>
    private static async Task<IResult> SyncCollectionAsync(
        HttpContext context, CalDavStore store, User user, Guid calendarId, XDocument body)
    {
        var requested = ReadRequestedProperties(body);
        var wantsData = requested.Contains(Cal + "calendar-data");

        var since = ParseSyncToken(body.Root!.Element(Dav + "sync-token")?.Value);

        var changes = await store.GetChangesAsync(calendarId, since, context.RequestAborted)
            .ConfigureAwait(false);

        var result = await BuildResourceResponsesAsync(
            context, store, user, calendarId, changes.Changed, wantsData).ConfigureAwait(false);

        // Silinenler ve yeni imleç aynı belgeye eklenir.
        if (result is not MultiStatusDocument document) return result;

        foreach (var uid in changes.Removed)
        {
            document.Document.Root!.Add(NotFoundResponse(ResourceHref(user.Id, calendarId, uid)));
        }

        document.Document.Root!.Add(new XElement(Dav + "sync-token", FormatSyncToken(changes.SyncToken)));
        return document;
    }

    private static async Task<IResult> BuildResourceResponsesAsync(
        HttpContext context,
        CalDavStore store,
        User user,
        Guid calendarId,
        IReadOnlyList<CalDavResourceInfo> resources,
        bool includeData)
    {
        var responses = new List<(string, IReadOnlyList<Property>)>(resources.Count);

        foreach (var resource in resources)
        {
            var properties = ResourceProperties(resource);

            if (includeData)
            {
                var content = await store.GetResourceAsync(calendarId, resource.Uid, context.RequestAborted)
                    .ConfigureAwait(false);

                if (content is { } found)
                {
                    properties = [.. properties, new Property(Cal + "calendar-data", found.Ics)];
                }
            }

            responses.Add((ResourceHref(user.Id, calendarId, resource.Uid), properties));
        }

        return new MultiStatusDocument(MultiStatus(responses));
    }

    // ==================================================================
    // GET / PUT / DELETE — kaynak işlemleri
    // ==================================================================

    private static async Task<IResult> GetResourceAsync(
        HttpContext context, CalDavStore store, Guid calendarId, string resource)
    {
        var user = CalDavAuthentication.RequireUser(context);

        var collection = await store.GetCollectionAsync(user.Id, calendarId, context.RequestAborted)
            .ConfigureAwait(false);

        if (collection is null) return Results.NotFound();

        var uid = TrimIcsSuffix(resource);
        var content = await store.GetResourceAsync(calendarId, uid, context.RequestAborted)
            .ConfigureAwait(false);

        if (content is not { } found) return Results.NotFound();

        context.Response.Headers.ETag = QuoteETag(found.ETag);
        return Results.Text(found.Ics, CalendarContentType);
    }

    private static async Task<IResult> PutResourceAsync(
        HttpContext context, CalDavStore store, Guid calendarId, string resource)
    {
        var user = CalDavAuthentication.RequireUser(context);

        using var reader = new StreamReader(context.Request.Body);
        var ics = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);

        var ifMatch = context.Request.Headers.IfMatch.ToString();
        var ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString();

        // If-None-Match: * → "yalnızca yoksa oluştur".
        if (ifNoneMatch.Trim() == "*")
        {
            var existing = await store
                .GetResourceAsync(calendarId, TrimIcsSuffix(resource), context.RequestAborted)
                .ConfigureAwait(false);

            if (existing is not null) return Results.StatusCode(StatusCodes.Status412PreconditionFailed);
        }

        var result = await store.PutResourceAsync(
            user.Id, calendarId, TrimIcsSuffix(resource), ics, ifMatch, context.RequestAborted)
            .ConfigureAwait(false);

        switch (result.Outcome)
        {
            case CalDavWriteOutcome.Created:
            case CalDavWriteOutcome.Updated:
                context.Response.Headers.ETag = QuoteETag(result.ETag!);
                return Results.StatusCode(result.Outcome == CalDavWriteOutcome.Created
                    ? StatusCodes.Status201Created
                    : StatusCodes.Status204NoContent);

            case CalDavWriteOutcome.Conflict:
                return Results.StatusCode(StatusCodes.Status412PreconditionFailed);

            case CalDavWriteOutcome.Forbidden:
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            case CalDavWriteOutcome.NotFound:
                return Results.NotFound();

            default:
                return Results.BadRequest(result.Message);
        }
    }

    private static async Task<IResult> DeleteResourceAsync(
        HttpContext context, CalDavStore store, Guid calendarId, string resource)
    {
        var user = CalDavAuthentication.RequireUser(context);

        var result = await store.DeleteResourceAsync(
            user.Id, calendarId, TrimIcsSuffix(resource),
            context.Request.Headers.IfMatch.ToString(), context.RequestAborted)
            .ConfigureAwait(false);

        return result.Outcome switch
        {
            CalDavWriteOutcome.Deleted => Results.NoContent(),
            CalDavWriteOutcome.Conflict => Results.StatusCode(StatusCodes.Status412PreconditionFailed),
            CalDavWriteOutcome.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
            _ => Results.NotFound(),
        };
    }

    // ==================================================================
    // Özellik kümeleri
    // ==================================================================

    private static List<Property> CollectionProperties(CalDavCalendarInfo collection, User user)
    {
        var calendar = collection.Calendar;

        return
        [
            new Property(Dav + "resourcetype",
                new object[] { new XElement(Dav + "collection"), new XElement(Cal + "calendar") }),
            new Property(Dav + "displayname", calendar.Name),
            new Property(Dav + "owner", new XElement(Dav + "href", PrincipalHref(user.Id))),
            new Property(Dav + "current-user-principal", new XElement(Dav + "href", PrincipalHref(user.Id))),
            new Property(Dav + "sync-token", FormatSyncToken(long.Parse(collection.CTag, CultureInfo.InvariantCulture))),
            new Property(CalendarServer + "getctag", collection.CTag),
            new Property(Cal + "calendar-description", calendar.Description ?? string.Empty),
            new Property(Cal + "calendar-timezone", null),
            new Property(Cal + "supported-calendar-component-set",
                new XElement(Cal + "comp", new XAttribute("name", "VEVENT"))),
            new Property(Apple + "calendar-color", ColorHex(calendar.Color)),
            new Property(Dav + "supported-report-set", SupportedReports()),
            new Property(Dav + "current-user-privilege-set", Privileges(collection.IsReadOnly)),
        ];
    }

    private static List<Property> ResourceProperties(CalDavResourceInfo resource) =>
    [
        new Property(Dav + "getetag", QuoteETag(resource.ETag)),
        new Property(Dav + "getcontenttype", "text/calendar; component=vevent"),
        new Property(Dav + "getlastmodified", HttpDate.Format(resource.LastModified)),
        new Property(Dav + "resourcetype", null),
    ];

    private static object[] SupportedReports() =>
    [
        Report(Cal + "calendar-query"),
        Report(Cal + "calendar-multiget"),
        Report(Dav + "sync-collection"),
    ];

    private static XElement Report(XName name)
        => new(Dav + "supported-report", new XElement(Dav + "report", new XElement(name)));

    private static object[] Privileges(bool readOnly)
    {
        var privileges = new List<XElement>
        {
            new(Dav + "privilege", new XElement(Dav + "read")),
        };

        if (!readOnly)
        {
            privileges.Add(new XElement(Dav + "privilege", new XElement(Dav + "write")));
            privileges.Add(new XElement(Dav + "privilege", new XElement(Dav + "write-content")));
            privileges.Add(new XElement(Dav + "privilege", new XElement(Dav + "bind")));
            privileges.Add(new XElement(Dav + "privilege", new XElement(Dav + "unbind")));
        }

        return [.. privileges];
    }

    // ==================================================================
    // Adresler ve ayrıştırma
    // ==================================================================

    private static string PrincipalHref(Guid userId) => $"/dav/p/{userId:N}/";

    private static string HomeHref(Guid userId) => $"/dav/c/{userId:N}/";

    private static string CollectionHref(Guid userId, Guid calendarId)
        => $"/dav/c/{userId:N}/{calendarId:N}/";

    private static string ResourceHref(Guid userId, Guid calendarId, string uid)
        => $"/dav/c/{userId:N}/{calendarId:N}/{Uri.EscapeDataString(uid)}.ics";

    private static string TrimIcsSuffix(string resource)
    {
        var name = Uri.UnescapeDataString(resource);
        return name.EndsWith(".ics", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static string? UidFromHref(string href)
    {
        var trimmed = href.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        if (slash < 0 || slash == trimmed.Length - 1) return null;

        return TrimIcsSuffix(trimmed[(slash + 1)..]);
    }

    /// <summary>Sunucu imleci istemciye opak bir URI olarak verilir.</summary>
    private static string FormatSyncToken(long token)
        => "http://takvim.local/ns/sync/" + token.ToString(CultureInfo.InvariantCulture);

    private static long ParseSyncToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return 0;

        var slash = token.LastIndexOf('/');
        var tail = slash >= 0 ? token[(slash + 1)..] : token;

        return long.TryParse(tail, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    /// <summary>İstenen tarih aralığını okur; belirtilmemişse null döner.</summary>
    private static (Instant? From, Instant? To) ReadTimeRange(XDocument body)
    {
        var range = body.Descendants(Cal + "time-range").FirstOrDefault();
        if (range is null) return (null, null);

        return (ParseCalDavInstant(range.Attribute("start")?.Value),
                ParseCalDavInstant(range.Attribute("end")?.Value));
    }

    private static Instant? ParseCalDavInstant(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var parsed = CalDavInstant.Parse(value);
        return parsed.Success ? parsed.Value : null;
    }

    /// <summary>Palet anahtarını Apple'ın beklediği onaltılık renge çevirir.</summary>
    private static string ColorHex(string color) => color switch
    {
        "tomato" => "#D50000FF",
        "flamingo" => "#E67C73FF",
        "tangerine" => "#F4511EFF",
        "banana" => "#F6BF26FF",
        "sage" => "#33B679FF",
        "basil" => "#0B8043FF",
        "peacock" => "#039BE5FF",
        "blueberry" => "#3F51B5FF",
        "lavender" => "#7986CBFF",
        "grape" => "#8E24AAFF",
        "graphite" => "#616161FF",
        _ => color.StartsWith('#') ? color : "#039BE5FF",
    };

    private static MultiStatusDocument MultiStatusResult(XDocument document) => new(document);

    /// <summary>207 Multi-Status yanıtı. Sonradan öğe eklenebilsin diye belgeyi taşır.</summary>
    private sealed class MultiStatusDocument(XDocument document) : IResult
    {
        public XDocument Document { get; } = document;

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            httpContext.Response.StatusCode = 207;
            httpContext.Response.ContentType = XmlContentType;

            await httpContext.Response
                .WriteAsync(Document.Declaration + Environment.NewLine + Document, httpContext.RequestAborted)
                .ConfigureAwait(false);
        }
    }
}
