namespace Takvim.Core.Domain;

/// <summary>
/// Değişiklik günlüğü. Üç ayrı ihtiyacı tek kayıtla karşılar:
/// <list type="number">
/// <item>Denetim kaydı — kim, neyi, ne zaman değiştirdi (silinenler dahil).</item>
/// <item>Çift yönlü senkronizasyon — istemciler son gördükleri <see cref="SyncToken"/>
/// değerinden sonrasını isteyerek artımlı güncelleme alır.</item>
/// <item>Geri alma — <see cref="BeforeJson"/> önceki hali barındırır.</item>
/// </list>
/// Bu tablo yalnızca eklenir; hiçbir satırı güncellenmez veya silinmez.
/// </summary>
public class ChangeLogEntry
{
    /// <summary>Artan sıra numarası. Senkronizasyon imleci olarak da kullanılır.</summary>
    public long SyncToken { get; set; }

    /// <summary>
    /// Tek bir kullanıcı eyleminin kimliği. "Bu ve sonrakiler" düzenlemesi gibi işlemler
    /// birden çok satıra dokunur; geri alma bu kimlikle hepsini birlikte çevirir.
    /// </summary>
    public Guid OperationId { get; set; }

    /// <summary>Değişen varlığın türü, ör. "Event", "Calendar".</summary>
    public required string EntityType { get; set; }

    public Guid EntityId { get; set; }

    /// <summary>Etkinlik değişikliklerinde hangi takvimi ilgilendirdiği; koleksiyon bazlı senkron için.</summary>
    public Guid? CalendarId { get; set; }

    public ChangeOperation Operation { get; set; }

    /// <summary>İşlemi yapan kullanıcı. Vekil erişiminde vekilin kimliği yazılır.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>Vekil bir kullanıcı adına işlem yaptıysa, adına işlem yapılan kullanıcı.</summary>
    public Guid? OnBehalfOfUserId { get; set; }

    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Değişiklikten önceki hal. Create işlemlerinde null.</summary>
    public string? BeforeJson { get; set; }

    /// <summary>Değişiklikten sonraki hal. Delete işlemlerinde null.</summary>
    public string? AfterJson { get; set; }

    /// <summary>Kullanıcıya gösterilecek özet, ör. "Etkinlik 14:00'ten 15:00'e taşındı".</summary>
    public string? Summary { get; set; }
}
