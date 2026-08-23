using Microsoft.EntityFrameworkCore;
using Takvim.Core.Domain;
using Takvim.Core.Time;
using Takvim.Data;

namespace Takvim.Server.State;

/// <summary>
/// İlk açılışta kullanıcıyı boş bir ekranla karşılamamak için gereken en az veriyi kurar:
/// bir kullanıcı, birkaç takvim ve başlangıç kategorileri.
/// Var olan veriye dokunmaz; her açılışta güvenle çağrılabilir.
/// </summary>
public sealed class CalendarBootstrapper(TakvimDbContext db)
{
    /// <summary>Faz 1 tek kullanıcılıdır; oturum açma gelene kadar bu kimlik kullanılır.</summary>
    public static readonly Guid LocalUserId = new("00000000-0000-0000-0000-000000000001");

    public async Task EnsureSeedDataAsync(CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == LocalUserId, ct).ConfigureAwait(false);

        if (user is null)
        {
            user = new User
            {
                Id = LocalUserId,
                DisplayName = Environment.UserName,
                Email = $"{Environment.UserName.ToLowerInvariant()}@takvim.local",
                TimeZoneId = DetectLocalZone(),
            };
            db.Users.Add(user);
        }

        if (!await db.Calendars.AnyAsync(c => c.OwnerUserId == LocalUserId, ct).ConfigureAwait(false))
        {
            db.Calendars.AddRange(
                new Calendar
                {
                    OwnerUserId = LocalUserId,
                    Name = "Kişisel",
                    Color = "peacock",
                    SortOrder = 0,
                    TimeZoneId = user.TimeZoneId,
                },
                new Calendar
                {
                    OwnerUserId = LocalUserId,
                    Name = "İş",
                    Color = "tomato",
                    SortOrder = 1,
                    TimeZoneId = user.TimeZoneId,
                },
                new Calendar
                {
                    OwnerUserId = LocalUserId,
                    Name = "Resmi Tatiller",
                    Color = "sage",
                    SortOrder = 99,
                    Kind = CalendarKind.Holiday,
                    IsReadOnly = true,
                    TimeZoneId = user.TimeZoneId,
                    DefaultReminderMinutes = null,
                    DefaultAllDayReminderMinutes = null,
                });
        }

        if (!await db.Categories.AnyAsync(c => c.OwnerUserId == LocalUserId, ct).ConfigureAwait(false))
        {
            db.Categories.AddRange(
                new Category { OwnerUserId = LocalUserId, Name = "Toplantı", Color = "blueberry", SortOrder = 0 },
                new Category { OwnerUserId = LocalUserId, Name = "Odak", Color = "grape", SortOrder = 1 },
                new Category { OwnerUserId = LocalUserId, Name = "Seyahat", Color = "banana", SortOrder = 2 },
                new Category { OwnerUserId = LocalUserId, Name = "Kişisel", Color = "flamingo", SortOrder = 3, IsPrivate = true });
        }

        if (!await db.WorkingHours.AnyAsync(w => w.UserId == LocalUserId, ct).ConfigureAwait(false))
        {
            db.WorkingHours.AddRange(WorkingHours.DefaultWeek(LocalUserId));
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// İşletim sisteminin zaman dilimini IANA kimliğine çevirir.
    /// Windows "Turkey Standard Time" der; bizim modelimiz "Europe/Istanbul" bekler.
    /// </summary>
    private static string DetectLocalZone()
    {
        var local = TimeZoneInfo.Local.Id;

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(local, out var iana)) return iana;
        return NodaTime.DateTimeZoneProviders.Tzdb.GetZoneOrNull(local) is not null
            ? local
            : TimeZoneService.DefaultZoneId;
    }
}
