using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;

namespace Takvim.Data.Services;

/// <summary>
/// Etkinlik şablonları: sık kurulan bir etkinliğin içeriğini bir kez doldurup
/// sonrakilerde tek tıkla getirmek.
/// </summary>
public sealed class TemplateService(TakvimDbContext db, IClock clock)
{
    /// <summary>Bir kullanıcının tutabileceği en fazla şablon sayısı.</summary>
    public const int MaxPerUser = 50;

    /// <summary>En çok kullanılan şablon üstte.</summary>
    public Task<List<EventTemplate>> GetAsync(Guid userId, CancellationToken ct = default)
        => db.EventTemplates
            .AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.UseCount)
            .ThenBy(t => t.Name)
            .ToListAsync(ct);

    public Task<EventTemplate?> FindAsync(Guid templateId, CancellationToken ct = default)
        => db.EventTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == templateId, ct);

    /// <summary>
    /// Şablonu kaydeder. Aynı adlı şablon varsa üzerine yazılır: kullanıcı aynı
    /// adı ikinci kez verdiğinde kastettiği şey güncellemektir.
    /// </summary>
    public async Task<EventTemplate?> SaveAsync(
        Guid userId,
        string name,
        EventTemplatePayload payload,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(payload);

        var trimmed = name.Trim();

        var existing = await db.EventTemplates
            .FirstOrDefaultAsync(t => t.UserId == userId && t.Name == trimmed, ct)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            existing.PayloadJson = EventTemplate.Write(payload);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            return existing;
        }

        var count = await db.EventTemplates.CountAsync(t => t.UserId == userId, ct).ConfigureAwait(false);
        if (count >= MaxPerUser) return null;

        var template = new EventTemplate
        {
            UserId = userId,
            Name = trimmed,
            PayloadJson = EventTemplate.Write(payload),
            CreatedAt = clock.GetCurrentInstant().ToDateTimeOffset(),
        };

        db.EventTemplates.Add(template);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return template;
    }

    /// <summary>Şablonun kullanıldığını işaretler; sıralama buna göre değişir.</summary>
    public async Task<EventTemplatePayload?> UseAsync(Guid templateId, CancellationToken ct = default)
    {
        var template = await db.EventTemplates
            .FirstOrDefaultAsync(t => t.Id == templateId, ct).ConfigureAwait(false);

        if (template is null) return null;

        template.UseCount++;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return template.Read();
    }

    public async Task RenameAsync(Guid templateId, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var template = await db.EventTemplates
            .FirstOrDefaultAsync(t => t.Id == templateId, ct).ConfigureAwait(false);

        if (template is null) return;

        template.Name = name.Trim();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid templateId, CancellationToken ct = default)
        => await db.EventTemplates
            .Where(t => t.Id == templateId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
}
