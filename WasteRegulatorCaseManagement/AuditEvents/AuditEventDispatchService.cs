using MongoDB.Driver;
using WasteRegulatorCaseManagement.Utils.Mongo;

namespace WasteRegulatorCaseManagement.AuditEvents;

/// <summary>
/// Reads audit events a process has not yet published, in sequence order,
/// and records each publishing outcome on the event.
/// </summary>
public sealed class AuditEventDispatchService(
    IMongoDbClientFactory connectionFactory,
    TimeProvider timeProvider,
    ILogger<AuditEventDispatchService> logger
)
{
    private const string FailedStatus = nameof(AuditEventDispatchStatus.Failed);

    private readonly IMongoCollection<AuditEvent> _auditEvents = connectionFactory.GetCollection<AuditEvent>(
        AuditEventService.AuditEventCollectionName
    );

    public async Task<IReadOnlyCollection<AuditEvent>> ReadUnsent(
        string processName,
        int batchSize,
        CancellationToken cancellationToken
    )
    {
        var utcNow = AuditEventTime.Now(timeProvider);
        var noDispatch = Builders<AuditEvent>.Filter.Exists(
            AuditEventDispatchFieldNames.DispatchPath(processName),
            false
        );
        var failedAndDue = Builders<AuditEvent>.Filter.And(
            Builders<AuditEvent>.Filter.Eq(AuditEventDispatchFieldNames.DispatchStatusPath(processName), FailedStatus),
            Builders<AuditEvent>.Filter.Lte(AuditEventDispatchFieldNames.DispatchNextAttemptAtPath(processName), utcNow)
        );

        // A stale read of dispatch state can send an event more than once. The
        // topic is at-least-once delivery, so consumers handle duplicates.
        return await _auditEvents
            .Find(Builders<AuditEvent>.Filter.Or(noDispatch, failedAndDue))
            .SortBy(x => x.Sequence)
            .Limit(batchSize)
            .ToListAsync(cancellationToken);
    }

    public Task MarkDispatched(string processName, AuditEvent auditEvent, CancellationToken cancellationToken) =>
        Mark(
            processName,
            auditEvent,
            new AuditEventDispatch
            {
                Status = AuditEventDispatchStatus.Dispatched,
                Date = AuditEventTime.Now(timeProvider),
                AttemptCount = ReadAttemptCount(processName, auditEvent) + 1,
            },
            outcome: "processed",
            cancellationToken
        );

    public Task MarkFailed(
        string processName,
        AuditEvent auditEvent,
        Exception exception,
        int maxDispatchAttempts,
        TimeSpan failedDispatchRetryDelay,
        CancellationToken cancellationToken
    )
    {
        var utcNow = AuditEventTime.Now(timeProvider);
        var attemptCount = ReadAttemptCount(processName, auditEvent) + 1;
        var status =
            attemptCount >= maxDispatchAttempts
                ? AuditEventDispatchStatus.DeadLettered
                : AuditEventDispatchStatus.Failed;

        return Mark(
            processName,
            auditEvent,
            new AuditEventDispatch
            {
                Status = status,
                Date = utcNow,
                Message = exception.Message,
                AttemptCount = attemptCount,
                NextAttemptAt = status is AuditEventDispatchStatus.Failed ? utcNow.Add(failedDispatchRetryDelay) : null,
            },
            outcome: "failed",
            cancellationToken
        );
    }

    private async Task Mark(
        string processName,
        AuditEvent auditEvent,
        AuditEventDispatch dispatch,
        string outcome,
        CancellationToken cancellationToken
    )
    {
        var filter = Builders<AuditEvent>.Filter.And(
            Builders<AuditEvent>.Filter.Eq(x => x.EventId, auditEvent.EventId),
            Builders<AuditEvent>.Filter.Or(
                Builders<AuditEvent>.Filter.Exists(AuditEventDispatchFieldNames.DispatchPath(processName), false),
                Builders<AuditEvent>.Filter.Eq(AuditEventDispatchFieldNames.DispatchStatusPath(processName), FailedStatus)
            )
        );

        var result = await _auditEvents.UpdateOneAsync(
            filter,
            Builders<AuditEvent>.Update.Set(AuditEventDispatchFieldNames.DispatchPath(processName), dispatch),
            cancellationToken: cancellationToken
        );

        if (result.ModifiedCount == 0)
        {
            logger.LogError(
                "Audit event {EventId} was {Outcome} but could not be marked with the dispatch outcome by {ProcessName}",
                auditEvent.EventId,
                outcome,
                processName
            );
        }
    }

    private static int ReadAttemptCount(string processName, AuditEvent auditEvent) =>
        auditEvent.Dispatches.TryGetValue(processName, out var dispatch) ? dispatch.AttemptCount : 0;
}
