using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Takvim.Core.Domain;
using Takvim.Core.Permissions;

namespace Takvim.Data.Services;

/// <summary>
/// Takvim paylaşımı işlemleri.
/// <para>
/// Arayüz paylaşım satırlarını doğrudan yazmaz; her değişiklik buradan geçer ve
/// denetim kaydına düşer. "Paylaşım ve erişim denetim kaydı" ancak tek bir
/// yazma yolu varsa güvenilirdir — arayüz kendi başına satır yazabiliyorsa
/// günlük eksik kalır.
/// </para>
/// </summary>
public sealed class SharingService(TakvimDbContext db)
{
    private readonly ChangeLogWriter _log = new(db);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Bir takvimin paylaşımları.</summary>
    public Task<List<CalendarShare>> GetSharesAsync(Guid calendarId, CancellationToken ct = default)
        => db.CalendarShares
            .AsNoTracking()
            .Include(s => s.Grantee)
            .Where(s => s.CalendarId == calendarId)
            .OrderBy(s => s.Grantee!.DisplayName)
            .ToListAsync(ct);

    /// <summary>Takvimi bir kullanıcıyla paylaşır. Zaten paylaşılmışsa seviyeyi günceller.</summary>
    public async Task<CalendarShare> ShareAsync(
        Guid calendarId,
        Guid granteeUserId,
        SharingLevel level,
        Guid actorUserId,
        CancellationToken ct = default)
    {
        var calendar = await db.Calendars
            .FirstOrDefaultAsync(c => c.Id == calendarId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Takvim bulunamadı: {calendarId}");

        var grantee = await db.Users
            .FirstOrDefaultAsync(u => u.Id == granteeUserId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Kullanıcı bulunamadı: {granteeUserId}");

        if (calendar.OwnerUserId == granteeUserId)
            throw new InvalidOperationException("Takvim sahibiyle paylaşılamaz.");

        var existing = await db.CalendarShares
            .FirstOrDefaultAsync(s => s.CalendarId == calendarId && s.GranteeUserId == granteeUserId, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            await UpdateAsync(existing.Id, actorUserId, share => share.Level = level, ct).ConfigureAwait(false);
            return existing;
        }

        var share = new CalendarShare
        {
            CalendarId = calendarId,
            GranteeUserId = granteeUserId,
            Level = level,
        };

        db.CalendarShares.Add(share);

        _log.Record(Guid.NewGuid(), ChangeOperation.Create, nameof(CalendarShare), share.Id,
            calendarId, beforeJson: null, afterJson: Snapshot(share), actorUserId,
            summary: $"\"{calendar.Name}\" takvimi {grantee.DisplayName} ile paylaşıldı ({Describe(level)})");

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return share;
    }

    /// <summary>Paylaşım koşullarını değiştirir.</summary>
    public async Task UpdateAsync(
        Guid shareId,
        Guid actorUserId,
        Action<CalendarShare> change,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        var share = await db.CalendarShares
            .Include(s => s.Calendar)
            .Include(s => s.Grantee)
            .FirstOrDefaultAsync(s => s.Id == shareId, ct).ConfigureAwait(false);

        if (share is null) return;

        var before = Snapshot(share);
        change(share);

        // Vekil değilse özel öğeleri görme yetkisi anlamsızdır; birlikte kapanır.
        if (!share.IsDelegate) share.CanSeePrivateItems = false;

        share.UpdatedAt = DateTimeOffset.UtcNow;

        _log.Record(Guid.NewGuid(), ChangeOperation.Update, nameof(CalendarShare), share.Id,
            share.CalendarId, before, Snapshot(share), actorUserId,
            summary: $"\"{share.Calendar?.Name}\" paylaşımı değişti: " +
                     $"{share.Grantee?.DisplayName} — {Describe(share.Level)}" +
                     (share.IsDelegate ? ", vekil" : ""));

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Paylaşımı kaldırır.</summary>
    public async Task RevokeAsync(Guid shareId, Guid actorUserId, CancellationToken ct = default)
    {
        var share = await db.CalendarShares
            .Include(s => s.Calendar)
            .Include(s => s.Grantee)
            .FirstOrDefaultAsync(s => s.Id == shareId, ct).ConfigureAwait(false);

        if (share is null) return;

        _log.Record(Guid.NewGuid(), ChangeOperation.Delete, nameof(CalendarShare), share.Id,
            share.CalendarId, Snapshot(share), afterJson: null, actorUserId,
            summary: $"\"{share.Calendar?.Name}\" takviminin {share.Grantee?.DisplayName} " +
                     "ile paylaşımı kaldırıldı");

        db.CalendarShares.Remove(share);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Bir takvimin paylaşım geçmişi; denetim görünümü için.</summary>
    public Task<List<ChangeLogEntry>> GetHistoryAsync(Guid calendarId, CancellationToken ct = default)
        => db.ChangeLog
            .AsNoTracking()
            .Where(c => c.EntityType == nameof(CalendarShare) && c.CalendarId == calendarId)
            .OrderByDescending(c => c.SyncToken)
            .ToListAsync(ct);

    public static string Describe(SharingLevel level) => level switch
    {
        SharingLevel.FreeBusy => "yalnızca meşgul/müsait",
        SharingLevel.TitleLocation => "başlık ve konum",
        SharingLevel.FullDetails => "tüm detaylar",
        SharingLevel.CanEdit => "değişiklik yapabilir",
        SharingLevel.FullControl => "tam yetki",
        _ => "erişim yok",
    };

    private static string Snapshot(CalendarShare share) => JsonSerializer.Serialize(new
    {
        share.CalendarId,
        share.GranteeUserId,
        share.Level,
        share.IsDelegate,
        share.CanSeePrivateItems,
    }, Json);
}
