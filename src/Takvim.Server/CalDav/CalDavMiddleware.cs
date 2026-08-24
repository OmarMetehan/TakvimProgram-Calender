using System.Net.Http.Headers;
using System.Text;
using Takvim.Core.Domain;
using Takvim.Data.Services;

namespace Takvim.Server.CalDav;

/// <summary>
/// CalDAV portundan yalnızca CalDAV yollarının yanıtlanmasını sağlar.
/// <para>
/// Sunucu yerel ağa açılabildiği için bu ayrım güvenlik sınırıdır: arayüzün
/// kendisi kimlik doğrulaması istemez, dolayısıyla o porttan asla
/// erişilebilmemelidir. Yalnızca <c>/dav</c> ve <c>/.well-known/caldav</c>
/// geçer, geri kalan her şey 404 alır.
/// </para>
/// </summary>
public sealed class CalDavPortIsolation(RequestDelegate next, CalDavOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var onCalDavPort = context.Connection.LocalPort == options.Port && options.Enabled;
        var path = context.Request.Path;
        var isCalDavPath = path.StartsWithSegments("/dav")
                        || path.StartsWithSegments("/.well-known/caldav");

        if (onCalDavPort && !isCalDavPath)
        {
            // Arayüz bu porttan görünmez.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!onCalDavPort && isCalDavPath && options.Binding == CalDavBinding.LocalNetwork)
        {
            // Ağa açıkken CalDAV yalnızca kendi portundan sunulur; arayüz portu
            // üzerinden ikinci bir giriş bırakılmaz.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}

/// <summary>
/// CalDAV isteklerinde HTTP Basic kimlik doğrulaması.
/// <para>
/// Kullanıcı adı e-posta, parola ise uygulama parolasıdır. Doğrulanan kullanıcı
/// <c>HttpContext.Items</c> içine konur; uç noktalar kimliği oradan okur ve
/// hiçbir uç nokta kimliği adres çubuğundan almaz.
/// </para>
/// </summary>
public sealed class CalDavAuthentication(RequestDelegate next)
{
    /// <summary>Doğrulanan kullanıcının <c>HttpContext.Items</c> içindeki anahtarı.</summary>
    public const string UserKey = "caldav.user";

    private const string Realm = "Takvim";

    public async Task InvokeAsync(HttpContext context, AppPasswordService passwords)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(passwords);

        if (!context.Request.Path.StartsWithSegments("/dav"))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var credentials = ReadBasicCredentials(context.Request.Headers.Authorization);

        if (credentials is null)
        {
            Challenge(context);
            return;
        }

        var user = await passwords
            .AuthenticateAsync(credentials.Value.Email, credentials.Value.Password, context.RequestAborted)
            .ConfigureAwait(false);

        if (user is null)
        {
            Challenge(context);
            return;
        }

        context.Items[UserKey] = user;
        await next(context).ConfigureAwait(false);
    }

    /// <summary>Doğrulanan kullanıcı; yoksa istek zaten reddedilmiştir.</summary>
    public static User RequireUser(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items[UserKey] as User
            ?? throw new InvalidOperationException("CalDAV isteği kimlik doğrulamasından geçmemiş.");
    }

    private static void Challenge(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = $"Basic realm=\"{Realm}\", charset=\"UTF-8\"";
    }

    private static (string Email, string Password)? ReadBasicCredentials(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;

        if (!AuthenticationHeaderValue.TryParse(header, out var parsed)
            || !string.Equals(parsed.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(parsed.Parameter))
        {
            return null;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parsed.Parameter));
        }
        catch (FormatException)
        {
            return null;
        }

        // Parolada iki nokta bulunabilir; yalnızca ilki ayırıcıdır.
        var separator = decoded.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0) return null;

        return (decoded[..separator], decoded[(separator + 1)..]);
    }
}
