using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace WasteRegulatorCaseManagement.AuditEvents;

/// <summary>
/// One change to an entity, written in the same transaction as the change
/// itself so an outbox processor can publish every change exactly as it
/// was committed.
/// </summary>
[BsonIgnoreExtraElements]
public sealed record AuditEvent
{
    [BsonId]
    public required string EventId { get; init; }

    public long Sequence { get; init; }
    public required string Entity { get; init; }
    public required string EntityId { get; init; }
    public required string Operation { get; init; }
    public required string EventType { get; init; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime OccurredAt { get; init; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime RecordedAt { get; init; }

    public required string Actor { get; init; }
    public int Version { get; init; }
    public BsonDocument? Before { get; init; }
    public BsonDocument? After { get; init; }
    public required string SchemaVersion { get; init; }

    /// <summary>Outcome of publishing this event, keyed by the publishing process's name.</summary>
    public Dictionary<string, AuditEventDispatch> Dispatches { get; init; } = [];
}
