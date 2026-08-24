using System.Text.Json;
using System.Text.Json.Serialization;

namespace Takvim.Core.Domain;

/// <summary>
/// Bir şablonun taşıdığı alanlar.
/// <para>
/// Tarih taşınmaz, <b>süre</b> taşınır: şablon "şu içerikte, şu kadar süren bir
/// etkinlik" demektir; ne zaman olacağını kullanıcı seçer.
/// </para>
/// </summary>
public sealed record EventTemplatePayload
{
    public string? Title { get; init; }
    public string? DescriptionHtml { get; init; }
    public string? AgendaText { get; init; }
    public string? PrivateNotes { get; init; }
    public string? LocationText { get; init; }

    /// <summary>Etkinliğin dakika cinsinden süresi.</summary>
    public int DurationMinutes { get; init; } = 60;

    public bool IsAllDay { get; init; }

    public string? Color { get; init; }
    public string? RecurrenceRule { get; init; }

    public Availability Availability { get; init; } = Availability.Busy;
    public EventVisibility Visibility { get; init; } = EventVisibility.Default;
    public HolidayBehavior HolidayBehavior { get; init; } = HolidayBehavior.Include;

    public bool IsForwardable { get; init; } = true;

    /// <summary>
    /// Şablona hangi sağlayıcıyla toplantı bağlantısı üretileceği. Bağlantının
    /// kendisi saklanmaz — her etkinlik kendi odasını almalı, yoksa iki ayrı
    /// toplantı aynı odaya düşer.
    /// </summary>
    public string? OnlineMeetingProvider { get; init; }

    public IReadOnlyList<Guid> CategoryIds { get; init; } = [];
    public IReadOnlyList<Guid> AttendeeUserIds { get; init; } = [];
    public IReadOnlyList<int> ReminderMinutes { get; init; } = [];
}

/// <summary>
/// Kullanıcının kaydettiği etkinlik şablonu.
/// <para>
/// İçerik JSON olarak tek sütunda tutulur. Alanları ayrı sütunlara açmak,
/// etkinliğe her yeni alan eklendiğinde ikinci bir göç gerektirirdi; şablon
/// zaten tek parça okunup tek parça yazılan bir nesnedir.
/// </para>
/// </summary>
public class EventTemplate
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid UserId { get; set; }

    /// <summary>Listede görünen ad, ör. "Haftalık ekip toplantısı".</summary>
    public required string Name { get; set; }

    /// <summary>Serileştirilmiş <see cref="EventTemplatePayload"/>.</summary>
    public required string PayloadJson { get; set; }

    public int UseCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// İçeriği çözer. Bozuk bir JSON şablonu kullanılamaz kılar ama uygulamayı
    /// çökertmemeli; o durumda boş bir şablon döner.
    /// </summary>
    public EventTemplatePayload Read()
    {
        try
        {
            return JsonSerializer.Deserialize<EventTemplatePayload>(PayloadJson, SerializerOptions)
                   ?? new EventTemplatePayload();
        }
        catch (JsonException)
        {
            return new EventTemplatePayload();
        }
    }

    public static string Write(EventTemplatePayload payload)
        => JsonSerializer.Serialize(payload, SerializerOptions);
}
