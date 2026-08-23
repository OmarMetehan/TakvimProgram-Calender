using Microsoft.EntityFrameworkCore;
using Takvim.Core.Domain;
using Takvim.Core.Text;

namespace Takvim.Data.Services;

/// <summary>Katılımcı seçicisinde gösterilen kişi.</summary>
/// <param name="UserId">Bu makinede hesabı varsa kimliği; yoksa null.</param>
public sealed record DirectoryEntry(Guid? UserId, string DisplayName, string Email)
{
    /// <summary>Listede gösterilecek metin.</summary>
    public string Label => $"{DisplayName} · {Email}";

    /// <summary>Yerel hesabı olmayan kişilerin müsaitliği bilinemez.</summary>
    public bool IsLocalUser => UserId is not null;
}

/// <summary>
/// Yerel kişi dizini. Bu uygulama tek makinede çalıştığı için "dizin", aynı
/// veritabanındaki kullanıcı kayıtlarıdır; kurumsal bir LDAP değildir.
/// <para>
/// Yerel hesabı olmayan biri de e-posta adresiyle davet edilebilir. Onun
/// müsaitliği bilinemez ve yanıtı elle işaretlenir; taşıma katmanı (e-posta)
/// eklenene kadar davet ona ulaşmaz. Arayüz bunu açıkça belirtir.
/// </para>
/// </summary>
public sealed class UserDirectory(TakvimDbContext db)
{
    /// <summary>Bu makinedeki tüm kullanıcılar.</summary>
    public Task<List<User>> GetUsersAsync(CancellationToken ct = default)
        => db.Users.AsNoTracking().OrderBy(u => u.DisplayName).ToListAsync(ct);

    /// <summary>Katılımcı arama: ada ve e-postaya göre süzer.</summary>
    public async Task<List<DirectoryEntry>> SearchAsync(
        string? term, int limit = 8, CancellationToken ct = default)
    {
        var users = await GetUsersAsync(ct).ConfigureAwait(false);

        var matches = users
            .Where(u => TurkishText.Contains(u.DisplayName, term) || TurkishText.Contains(u.Email, term))
            .Take(limit)
            .Select(u => new DirectoryEntry(u.Id, u.DisplayName, u.Email));

        return [.. matches];
    }

    public Task<User?> FindAsync(Guid userId, CancellationToken ct = default)
        => db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);

    public Task<User?> FindByEmailAsync(string email, CancellationToken ct = default)
        => db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email, ct);

    /// <summary>
    /// Yeni bir yerel kullanıcı açar ve ona bir kişisel takvim verir.
    /// Takvimsiz kullanıcı hiçbir şey yapamaz; ikisi birlikte kurulur.
    /// </summary>
    public async Task<User> CreateUserAsync(
        string displayName, string email, string timeZoneId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var normalizedEmail = email.Trim().ToLowerInvariant();

        if (await db.Users.AnyAsync(u => u.Email == normalizedEmail, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"Bu e-posta zaten kayıtlı: {normalizedEmail}");

        var user = new User
        {
            DisplayName = displayName.Trim(),
            Email = normalizedEmail,
            TimeZoneId = timeZoneId,
        };

        db.Users.Add(user);
        db.Calendars.Add(new Calendar
        {
            OwnerUserId = user.Id,
            Name = "Kişisel",
            Color = "peacock",
            TimeZoneId = timeZoneId,
        });
        db.WorkingHours.AddRange(WorkingHours.DefaultWeek(user.Id));

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return user;
    }

    /// <summary>Kullanıcıyı ve ona ait her şeyi kalıcı olarak siler.</summary>
    public async Task DeleteUserAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct).ConfigureAwait(false);
        if (user is null) return;

        db.Users.Remove(user);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
