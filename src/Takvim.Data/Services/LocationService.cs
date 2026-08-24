using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;
using Takvim.Core.Text;

namespace Takvim.Data.Services;

/// <summary>
/// Konum önerileri. Kullanıcı bir etkinliği kaydettiğinde konumu buraya
/// düşer; bir sonraki sefere yazmaya başlar başlamaz öneri olarak çıkar.
/// </summary>
public sealed class LocationService(TakvimDbContext db, IClock clock)
{
    /// <summary>Öneri listesinde gösterilecek en fazla konum sayısı.</summary>
    public const int SuggestionLimit = 12;

    /// <summary>Bu uzunluğun altındaki metinler hatırlanmaz; "A" bir konum değildir.</summary>
    private const int MinimumLength = 2;

    /// <summary>Sabitlenmişler önce, sonra en çok kullanılanlar.</summary>
    public Task<List<SavedLocation>> SuggestAsync(Guid userId, CancellationToken ct = default)
        => db.SavedLocations
            .AsNoTracking()
            .Where(l => l.UserId == userId)
            .OrderByDescending(l => l.IsPinned)
            .ThenByDescending(l => l.UseCount)
            .ThenByDescending(l => l.LastUsedAt)
            .Take(SuggestionLimit)
            .ToListAsync(ct);

    /// <summary>
    /// Konumu kullanıldı olarak işaretler; yoksa açar. Etkinlik kaydından sonra
    /// çağrılır ve sessizce çalışır — burada bir hata etkinliği kaydetmiş
    /// olmayı geçersiz kılmamalı.
    /// </summary>
    public async Task RecordAsync(Guid userId, string? locationText, CancellationToken ct = default)
    {
        var text = locationText?.Trim();
        if (text is null || text.Length < MinimumLength) return;

        var normalized = TurkishText.Normalize(text);
        if (normalized.Length < MinimumLength) return;

        var existing = await db.SavedLocations
            .FirstOrDefaultAsync(l => l.UserId == userId && l.NormalizedText == normalized, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            db.SavedLocations.Add(new SavedLocation
            {
                UserId = userId,
                Text = text,
                NormalizedText = normalized,
                LastUsedAt = clock.GetCurrentInstant().ToDateTimeOffset(),
            });
        }
        else
        {
            existing.UseCount++;
            existing.LastUsedAt = clock.GetCurrentInstant().ToDateTimeOffset();

            // Kullanıcı yazımı değiştirmişse (büyük harf, kısaltma) son hâli kalır.
            existing.Text = text;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Konumu listenin başına sabitler ya da sabitlemeyi kaldırır.</summary>
    public async Task<bool> TogglePinAsync(Guid locationId, CancellationToken ct = default)
    {
        var location = await db.SavedLocations
            .FirstOrDefaultAsync(l => l.Id == locationId, ct).ConfigureAwait(false);

        if (location is null) return false;

        location.IsPinned = !location.IsPinned;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return location.IsPinned;
    }

    /// <summary>Konumu önerilerden çıkarır.</summary>
    public async Task ForgetAsync(Guid locationId, CancellationToken ct = default)
    {
        await db.SavedLocations
            .Where(l => l.Id == locationId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }
}
