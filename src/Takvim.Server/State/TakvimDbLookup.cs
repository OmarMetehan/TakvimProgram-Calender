using Microsoft.EntityFrameworkCore;
using Takvim.Data;

namespace Takvim.Server.State;

/// <summary>
/// Arka plan görevlerinin ihtiyaç duyduğu tekil okumalar.
/// <para>
/// Arayüz durumu devre başınadır ve arka planda yoktur; bu küçük yardımcı,
/// zamanlayıcıların kullanıcı ayarlarına ulaşmasını sağlar.
/// </para>
/// </summary>
public sealed class TakvimDbLookup(TakvimDbContext db)
{
    /// <summary>Kullanıcının zaman dilimi; bulunamazsa boş dize.</summary>
    public async Task<string> ZoneOfAsync(Guid userId, CancellationToken ct = default)
        => await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.TimeZoneId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? string.Empty;
}
