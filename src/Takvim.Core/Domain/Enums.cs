namespace Takvim.Core.Domain;

/// <summary>Takvimin türü. Kaynak ve abone takvimler farklı kurallara tabidir.</summary>
public enum CalendarKind
{
    /// <summary>Kullanıcının kendi takvimi.</summary>
    Personal = 0,
    /// <summary>Ekip veya birim takvimi (Faz 2).</summary>
    Team = 1,
    /// <summary>Oda/ekipman kaynak takvimi (Faz 3).</summary>
    Resource = 2,
    /// <summary>Dış ICS beslemesine abonelik; salt okunur.</summary>
    Subscribed = 3,
    /// <summary>Yerleşik resmi tatil takvimi; salt okunur.</summary>
    Holiday = 4,
}

/// <summary>Etkinliğin kullanıcıyı meşgul gösterip göstermediği. Serbest/meşgul sorgularının temeli.</summary>
public enum Availability
{
    Busy = 0,
    Free = 1,
    Tentative = 2,
    OutOfOffice = 3,
    WorkingElsewhere = 4,
    FocusTime = 5,
}

/// <summary>Etkinliğin başkalarınca ne kadar görülebileceği. iCalendar CLASS alanına eşlenir.</summary>
public enum EventVisibility
{
    /// <summary>Takvimin paylaşım seviyesi ne diyorsa o.</summary>
    Default = 0,
    /// <summary>Takvim gizli olsa bile detaylar görünür.</summary>
    Public = 1,
    /// <summary>Takvim paylaşılmış olsa bile yalnızca meşgul görünür.</summary>
    Private = 2,
}

/// <summary>iCalendar STATUS alanı.</summary>
public enum EventStatus
{
    Confirmed = 0,
    Tentative = 1,
    Cancelled = 2,
}

/// <summary>Hatırlatıcının hangi kanaldan gideceği.</summary>
public enum ReminderChannel
{
    /// <summary>Uygulama içi / masaüstü bildirimi.</summary>
    InApp = 0,
    Email = 1,
    Push = 2,
}

/// <summary>
/// Tekrarlayan bir etkinliğin resmi tatile denk gelen örneğine ne olacağı.
/// </summary>
public enum HolidayBehavior
{
    /// <summary>Tatilde de olsa örnek üretilir.</summary>
    Include = 0,

    /// <summary>Tatile denk gelen örnek hiç üretilmez.</summary>
    Skip = 1,

    /// <summary>
    /// Örnek, tatilden sonraki ilk iş gününe ertelenir. Hafta sonları da
    /// atlanır: pazartesi toplantısını cumartesiye taşımak işe yaramaz.
    /// </summary>
    MoveToNextWorkingDay = 2,
}

/// <summary>Değişiklik günlüğüne yazılan işlem türü.</summary>
public enum ChangeOperation
{
    Create = 0,
    Update = 1,
    Delete = 2,
    /// <summary>Çöp kutusundan geri alma.</summary>
    Restore = 3,
}
