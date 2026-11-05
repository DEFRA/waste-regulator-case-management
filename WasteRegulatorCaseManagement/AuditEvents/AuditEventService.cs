using MongoDB.Driver;
using NUlid;
using WasteRegulatorCaseManagement.Utils.Mongo;

namespace WasteRegulatorCaseManagement.AuditEvents;

/// <summary>
/// Records audit events inside the caller's transaction, numbering them
/// from a single counter so the outbox can publish them in commit order.
/// </summary>
public sealed class AuditEventService
{
    public const string AuditEventCollectionName = "auditEvents";
    public const string AuditEventCounterCollectionName = "auditEventCounters";

    private const string AuditEventCounterId = "audit_event";

    private readonly IMongoCollection<AuditEvent> _auditEvents;
    private readonly IMongoCollection<AuditEventCounter> _counters;
    private readonly TimeProvider _timeProvider;

    public AuditEventService(IMongoDbClientFactory connectionFactory, TimeProvider timeProvider)
    {
        _auditEvents = connectionFactory.GetCollection<AuditEvent>(AuditEventCollectionName);
        _counters = connectionFactory.GetCollection<AuditEventCounter>(AuditEventCounterCollectionName);
        _timeProvider = timeProvider;
        _auditEvents.Indexes.CreateMany(
            [
                new CreateIndexModel<AuditEvent>(
                    Builders<AuditEvent>.IndexKeys.Ascending(x => x.Sequence),
                    new CreateIndexOptions { Name = "Sequence", Unique = true }
                ),
                new CreateIndexModel<AuditEvent>(
                    Builders<AuditEvent>
                        .IndexKeys.Ascending(x => x.Entity)
                        .Ascending(x => x.EntityId)
                        .Ascending(x => x.Version),
                    new CreateIndexOptions { Name = "Entity_EntityId_Version" }
                ),
            ]
        );
    }

    public async Task RecordEvent(
        IClientSessionHandle session,
        AuditEventRequest auditEvent,
        CancellationToken cancellationToken
    )
    {
        var counter = await _counters.FindOneAndUpdateAsync(
            session,
            Builders<AuditEventCounter>.Filter.Eq(x => x.Id, AuditEventCounterId),
            Builders<AuditEventCounter>.Update.Inc(x => x.Sequence, 1),
            new FindOneAndUpdateOptions<AuditEventCounter> { IsUpsert = true, ReturnDocument = ReturnDocument.After },
            cancellationToken
        );

        await _auditEvents.InsertOneAsync(
            session,
            new AuditEvent
            {
                EventId = Ulid.NewUlid().ToString(),
                Sequence = counter.Sequence,
                Entity = auditEvent.Entity,
                EntityId = auditEvent.EntityId,
                Operation = ToValue(auditEvent.Operation),
                EventType = auditEvent.EventType,
                OccurredAt = auditEvent.OccurredAt,
                RecordedAt = WithoutMicroseconds(_timeProvider.GetUtcNow()),
                Actor = auditEvent.Actor,
                Version = auditEvent.Version,
                Before = auditEvent.Before,
                After = auditEvent.After,
                SchemaVersion = auditEvent.SchemaVersion,
            },
            cancellationToken: cancellationToken
        );
    }

    private static DateTime WithoutMicroseconds(DateTimeOffset now) =>
        new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero).UtcDateTime;

    private static string ToValue(AuditEventOperation operation) =>
        operation switch
        {
            AuditEventOperation.Insert => "insert",
            AuditEventOperation.Update => "update",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };
}
