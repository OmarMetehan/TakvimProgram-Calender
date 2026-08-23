using System.Text.Json;
using Takvim.Core.Domain;

namespace Takvim.Data.Services;

/// <summary>
/// Değişiklik günlüğüne yazan tek nokta. Denetim kaydı, artımlı senkronizasyon
/// ve geri alma aynı satırlardan beslenir; bu yüzden hiçbir yazma yolu bunu atlamaz.
/// </summary>
public sealed class ChangeLogWriter(TakvimDbContext db)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Bir etkinlik değişikliğini kaydeder. Çağıran <c>SaveChanges</c> yapmakla yükümlüdür.</summary>
    public void RecordEvent(
        Guid operationId,
        ChangeOperation operation,
        Guid eventId,
        Guid calendarId,
        EventSnapshot? before,
        EventSnapshot? after,
        Guid? actorUserId,
        string summary,
        Guid? onBehalfOfUserId = null)
    {
        db.ChangeLog.Add(new ChangeLogEntry
        {
            OperationId = operationId,
            EntityType = nameof(Event),
            EntityId = eventId,
            CalendarId = calendarId,
            Operation = operation,
            ActorUserId = actorUserId,
            OnBehalfOfUserId = onBehalfOfUserId,
            ChangedAt = DateTimeOffset.UtcNow,
            BeforeJson = before is null ? null : JsonSerializer.Serialize(before, Json),
            AfterJson = after is null ? null : JsonSerializer.Serialize(after, Json),
            Summary = summary,
        });
    }

    /// <summary>Etkinlik dışı varlıklar (takvim, kategori) için genel kayıt.</summary>
    public void Record(
        Guid operationId,
        ChangeOperation operation,
        string entityType,
        Guid entityId,
        Guid? calendarId,
        string? beforeJson,
        string? afterJson,
        Guid? actorUserId,
        string summary)
    {
        db.ChangeLog.Add(new ChangeLogEntry
        {
            OperationId = operationId,
            EntityType = entityType,
            EntityId = entityId,
            CalendarId = calendarId,
            Operation = operation,
            ActorUserId = actorUserId,
            ChangedAt = DateTimeOffset.UtcNow,
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            Summary = summary,
        });
    }

    public static EventSnapshot? Deserialize(string? json)
        => json is null ? null : JsonSerializer.Deserialize<EventSnapshot>(json, Json);
}
