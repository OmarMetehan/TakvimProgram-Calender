using System.Text.Json;
using System.Text.Json.Serialization;

namespace Takvim.Core.Domain;

/// <summary>Kaydedilmiş aramanın tarih aralığı. Mutlak tarih değil, kayan bir pencere.</summary>
public enum SearchRange
{
    /// <summary>Bugünden ileriye.</summary>
    Upcoming,

    /// <summary>Bugünden geriye.</summary>
    Past,

    /// <summary>İçinde bulunulan yıl.</summary>
    ThisYear,

    /// <summary>Geçmiş ve gelecek; pratikte on yıllık pencere.</summary>
    All,
}

/// <summary>
/// Bir aramanın ölçütleri.
/// <para>
/// Tarihler mutlak değil <see cref="SearchRange"/> olarak saklanır: "gelecek
/// toplantılarım" araması yarın da gelecek toplantıları göstermeli, kaydedildiği
/// günün gelecekteki toplantılarını değil.
/// </para>
/// </summary>
public sealed record SearchCriteria
{
    public string? Term { get; init; }

    public SearchRange Range { get; init; } = SearchRange.Upcoming;

    public IReadOnlyList<Guid> CalendarIds { get; init; } = [];
    public IReadOnlyList<Guid> CategoryIds { get; init; } = [];
    public IReadOnlyList<Availability> Availabilities { get; init; } = [];

    public Guid? OrganizerUserId { get; init; }
    public Guid? AttendeeUserId { get; init; }

    public bool? HasAttendees { get; init; }
    public bool? IsRecurring { get; init; }
    public bool? IsAllDay { get; init; }
    public bool? HasAttachments { get; init; }
    public bool? HasOnlineMeeting { get; init; }

    public bool IncludeCancelled { get; init; }

    /// <summary>Hiçbir ölçüt verilmemişse arama çalıştırılmaz.</summary>
    public bool IsEmpty
        => string.IsNullOrWhiteSpace(Term)
           && CalendarIds.Count == 0
           && CategoryIds.Count == 0
           && Availabilities.Count == 0
           && OrganizerUserId is null
           && AttendeeUserId is null
           && HasAttendees is null
           && IsRecurring is null
           && IsAllDay is null
           && HasAttachments is null
           && HasOnlineMeeting is null;
}

/// <summary>
/// Kullanıcının kaydettiği arama. Ölçütler JSON olarak tek sütunda tutulur;
/// gerekçesi <see cref="EventTemplate"/> ile aynıdır.
/// </summary>
public class SavedSearch
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }

    public required string Name { get; set; }

    /// <summary>Serileştirilmiş <see cref="SearchCriteria"/>.</summary>
    public required string CriteriaJson { get; set; }

    public int UseCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Bozuk JSON aramayı kullanılamaz kılar ama uygulamayı çökertmez.</summary>
    public SearchCriteria Read()
    {
        try
        {
            return JsonSerializer.Deserialize<SearchCriteria>(CriteriaJson, SerializerOptions)
                   ?? new SearchCriteria();
        }
        catch (JsonException)
        {
            return new SearchCriteria();
        }
    }

    public static string Write(SearchCriteria criteria)
        => JsonSerializer.Serialize(criteria, SerializerOptions);
}
