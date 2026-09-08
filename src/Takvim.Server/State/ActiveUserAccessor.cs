namespace Takvim.Server.State;

/// <summary>
/// Uygulamada o an açık olan hesap.
/// <para>
/// Arka plan servisleri Blazor devresini görmez; hatırlatıcı yoklaması ise
/// "kimin hatırlatıcısı" sorusunu yanıtlamak zorundadır. Bu tek örnek, arayüzün
/// bildiği aktif hesabı arka plana taşır.
/// </para>
/// <para>
/// Tek pencere, tek aktif hesap: uygulama masaüstünde tek bir pencerede açılır
/// ve hesap değiştirmek pencerenin tamamını değiştirir. Aynı anda iki hesabın
/// açık olduğu bir durum yoktur.
/// </para>
/// </summary>
public sealed class ActiveUserAccessor
{
    private readonly Lock _gate = new();
    private Guid _userId = CalendarBootstrapper.LocalUserId;

    /// <summary>
    /// Aktif hesabın kimliği. Arayüz devresi yazar, arka plan görevleri okur;
    /// kilit, iki iş parçacığının yarım yazılmış bir <c>Guid</c> görmesini önler.
    /// </summary>
    public Guid UserId
    {
        get { lock (_gate) return _userId; }
        set { lock (_gate) _userId = value; }
    }
}
