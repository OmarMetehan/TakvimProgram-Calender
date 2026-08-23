using Takvim.Core.Permissions;

namespace Takvim.Core.Domain;

/// <summary>
/// Bir takvimin başka bir yerel kullanıcıyla paylaşımı.
/// <para>
/// İzin motorunun (<see cref="CalendarAccess"/>) girdisidir: paylaşım seviyesi,
/// vekillik ve özel öğeleri görme yetkisi buradan okunur. Satır yoksa takvim
/// o kullanıcıyla paylaşılmamış demektir ve hiçbir şey görünmez.
/// </para>
/// </summary>
public class CalendarShare
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid CalendarId { get; set; }
    public Calendar? Calendar { get; set; }

    /// <summary>Erişim verilen kullanıcı.</summary>
    public Guid GranteeUserId { get; set; }
    public User? Grantee { get; set; }

    public SharingLevel Level { get; set; } = SharingLevel.FreeBusy;

    /// <summary>
    /// Vekil erişimi: kullanıcı, takvim sahibinin adına hareket eder.
    /// Yaptığı her işlem denetim kaydına "kimin adına" bilgisiyle yazılır.
    /// </summary>
    public bool IsDelegate { get; set; }

    /// <summary>
    /// Vekilin özel işaretli etkinlikleri ve gizli kategorileri görüp göremeyeceği.
    /// Kapalıysa bunlar yalnızca "Meşgul" olarak görünür.
    /// </summary>
    public bool CanSeePrivateItems { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
