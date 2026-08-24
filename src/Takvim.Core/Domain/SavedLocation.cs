namespace Takvim.Core.Domain;

/// <summary>
/// Kullanıcının daha önce kullandığı bir konum.
/// <para>
/// Ayrı bir "konum defteri" tutulmaz; kullanılan konumlar kaydedilirken
/// kendiliğinden buraya yazılır ve sonraki etkinliklerde öneri olarak çıkar.
/// Kullanım sayısı sıralamayı belirler: en çok gidilen yer en üstte.
/// </para>
/// </summary>
public class SavedLocation
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }

    /// <summary>Kullanıcının yazdığı metnin kendisi; etkinliğe aynen geçer.</summary>
    public required string Text { get; set; }

    /// <summary>
    /// Karşılaştırma için normalleştirilmiş hâli. "Toplantı Odası" ile
    /// "toplanti odasi" aynı konumdur.
    /// </summary>
    public required string NormalizedText { get; set; }

    public int UseCount { get; set; } = 1;

    public DateTimeOffset LastUsedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Kullanıcının listeye sabitlediği konum; kullanım sayısından bağımsız olarak üstte kalır.</summary>
    public bool IsPinned { get; set; }
}
