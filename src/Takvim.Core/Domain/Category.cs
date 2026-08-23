namespace Takvim.Core.Domain;

/// <summary>
/// Kategori etiketi. Renkten bağımsızdır: bir etkinlik birden çok kategori taşıyabilir
/// ve kategoriler filtrelemede kullanılır. iCalendar CATEGORIES alanına eşlenir.
/// </summary>
public class Category
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid OwnerUserId { get; set; }
    public User? Owner { get; set; }

    public required string Name { get; set; }

    /// <summary>Rozet rengi. Etkinliğin kendi renginden ayrıdır.</summary>
    public string Color { get; set; } = "graphite";

    /// <summary>
    /// True ise bu kategoriyi taşıyan etkinlikler paylaşımlarda ve vekil erişiminde gizlenir.
    /// İzin modelindeki dördüncü boyut budur.
    /// </summary>
    public bool IsPrivate { get; set; }

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<EventCategory> Events { get; set; } = [];
}

/// <summary>Etkinlik ile kategori arasındaki çoktan çoğa bağ.</summary>
public class EventCategory
{
    public Guid EventId { get; set; }
    public Event? Event { get; set; }

    public Guid CategoryId { get; set; }
    public Category? Category { get; set; }
}
