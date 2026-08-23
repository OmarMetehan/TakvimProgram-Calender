using NodaTime;

namespace Takvim.Core.Domain;

/// <summary>Kullanıcının o gün nerede çalıştığı. Ekip görünümlerinde paylaşılır.</summary>
public enum WorkLocation
{
    /// <summary>Belirtilmemiş.</summary>
    Unspecified = 0,
    Office = 1,
    Home = 2,
    /// <summary>Şube ya da başka bir ofis.</summary>
    Branch = 3,
    /// <summary>Çalışmıyor: izin, tatil.</summary>
    Away = 4,
}

/// <summary>
/// Bir kullanıcının haftanın belirli bir günündeki mesai aralığı.
/// Günler ayrı satırlarda tutulur; çünkü çalışma saatleri gün bazında
/// farklılaşabilir ve bazı günler hiç çalışılmaz.
/// </summary>
public class WorkingHours
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public IsoDayOfWeek DayOfWeek { get; set; }

    /// <summary>False ise o gün çalışılmaz; ızgarada gün tümüyle soluk gösterilir.</summary>
    public bool IsWorkingDay { get; set; } = true;

    public LocalTime Start { get; set; } = new(9, 0);

    public LocalTime End { get; set; } = new(18, 0);

    /// <summary>Öğle arası başlangıcı. Null ise ara gösterilmez.</summary>
    public LocalTime? BreakStart { get; set; }

    public LocalTime? BreakEnd { get; set; }

    /// <summary>Türkiye'de standart hafta: pazartesi-cuma 09:00-18:00.</summary>
    public static IEnumerable<WorkingHours> DefaultWeek(Guid userId)
    {
        foreach (var day in Enum.GetValues<IsoDayOfWeek>())
        {
            if (day == IsoDayOfWeek.None) continue;

            yield return new WorkingHours
            {
                UserId = userId,
                DayOfWeek = day,
                IsWorkingDay = day <= IsoDayOfWeek.Friday,
                Start = new LocalTime(9, 0),
                End = new LocalTime(18, 0),
            };
        }
    }
}

/// <summary>
/// Belirli bir tarihteki çalışma konumu. Yalnızca varsayılandan sapan günler
/// için satır açılır; her gün için kayıt tutulmaz.
/// </summary>
public class WorkLocationEntry
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public LocalDate Date { get; set; }

    public WorkLocation Location { get; set; }

    /// <summary>Şube adı gibi serbest metin.</summary>
    public string? Note { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
