using Microsoft.EntityFrameworkCore;
using NodaTime;
using Takvim.Core.Domain;

namespace Takvim.Data.Services;

/// <summary>Kategori oluşturma ve düzenleme girdisi.</summary>
public sealed record CategoryInput
{
    public required string Name { get; init; }

    public string Color { get; init; } = "graphite";

    /// <summary>
    /// True ise bu kategoriyi taşıyan etkinlikler paylaşımlarda ve vekil
    /// erişiminde gizlenir. İzin modelinin dördüncü boyutu budur.
    /// </summary>
    public bool IsPrivate { get; init; }
}

/// <summary>
/// Kategori yönetimi.
/// <para>
/// Kategoriler takvimlerden bağımsızdır: bir etkinlik tam olarak bir takvime
/// ait ama birden çok kategoriye sahip olabilir. Bu yüzden silme de farklı
/// çalışır — kategori silinince etkinlikler silinmez, yalnızca etiketleri
/// düşer.
/// </para>
/// </summary>
public sealed class CategoryService(TakvimDbContext db, IClock clock)
{
    /// <summary>Bir kullanıcının açabileceği en fazla kategori.</summary>
    public const int MaxPerUser = 60;

    // ==================================================================
    // Okuma
    // ==================================================================

    public Task<List<Category>> GetOwnAsync(Guid userId, CancellationToken ct = default)
        => db.Categories
            .AsNoTracking()
            .Where(c => c.OwnerUserId == userId)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .ToListAsync(ct);

    /// <summary>Kategoriyi kaç etkinliğin taşıdığı; silmeden önce gösterilir.</summary>
    public Task<int> GetUsageAsync(Guid categoryId, CancellationToken ct = default)
        => db.EventCategories
            .CountAsync(ec => ec.CategoryId == categoryId && ec.Event!.DeletedAt == null, ct);

    // ==================================================================
    // Yazma
    // ==================================================================

    public async Task<Category> CreateAsync(
        Guid userId, CategoryInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Name);

        var name = input.Name.Trim();

        // Aynı adlı ikinci kategori, süzgeçte hangisinin hangisi olduğunu
        // anlaşılmaz kılardı.
        var exists = await db.Categories
            .AnyAsync(c => c.OwnerUserId == userId && c.Name == name, ct).ConfigureAwait(false);

        if (exists) throw new InvalidOperationException($"\"{name}\" adlı bir kategori zaten var.");

        var count = await db.Categories
            .CountAsync(c => c.OwnerUserId == userId, ct).ConfigureAwait(false);

        if (count >= MaxPerUser)
        {
            throw new InvalidOperationException($"En fazla {MaxPerUser} kategori açılabilir.");
        }

        var category = new Category
        {
            OwnerUserId = userId,
            Name = name,
            Color = input.Color,
            IsPrivate = input.IsPrivate,
            SortOrder = count,
            CreatedAt = clock.GetCurrentInstant().ToDateTimeOffset(),
        };

        db.Categories.Add(category);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return category;
    }

    /// <returns>Güncellendiyse null, güncellenemediyse gerekçesi.</returns>
    public async Task<string?> UpdateAsync(
        Guid categoryId, CategoryInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Name);

        var category = await db.Categories
            .FirstOrDefaultAsync(c => c.Id == categoryId, ct).ConfigureAwait(false);

        if (category is null) return "Kategori bulunamadı.";

        var name = input.Name.Trim();

        var clash = await db.Categories
            .AnyAsync(c => c.OwnerUserId == category.OwnerUserId
                        && c.Name == name
                        && c.Id != categoryId, ct).ConfigureAwait(false);

        if (clash) return $"\"{name}\" adlı bir kategori zaten var.";

        category.Name = name;
        category.Color = input.Color;
        category.IsPrivate = input.IsPrivate;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return null;
    }

    public async Task ReorderAsync(
        Guid userId, IReadOnlyList<Guid> orderedIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);

        var categories = await db.Categories
            .Where(c => c.OwnerUserId == userId)
            .ToListAsync(ct).ConfigureAwait(false);

        for (var i = 0; i < orderedIds.Count; i++)
        {
            if (categories.Find(c => c.Id == orderedIds[i]) is { } category) category.SortOrder = i;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Kategoriyi siler. Etkinlikler silinmez, yalnızca etiketleri düşer —
    /// kategori bir sınıflandırmadır, içinde bir şey barındırmaz.
    /// <para>
    /// Çöp kutusu yok: geri alınacak bir içerik olmadığı için, silinen bir
    /// kategoriyi geri getirmek onu yeniden yazmakla aynı şeydir.
    /// </para>
    /// </summary>
    public async Task DeleteAsync(Guid categoryId, CancellationToken ct = default)
        => await db.Categories
            .Where(c => c.Id == categoryId)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
}
