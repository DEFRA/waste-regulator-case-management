namespace WasteRegulatorCaseManagement.AuditEvents.Analytics;

/// <summary>
/// The entities, operations, event types, actors and schema versions the
/// analytics topic accepts. An audit event outside it is never published.
/// </summary>
public static class AnalyticsEventVocabulary
{
    private const string CaseEntity = "case";
    private const string CreateOperation = "create";
    private const string UpdateOperation = "update";

    private static readonly HashSet<string> s_eventTypes =
    [
        "case-created",
        "state-changed",
        "assigned",
        "unassigned",
        "note-added",
    ];

    private static readonly HashSet<string> s_actorPrefixes = ["service", "user", "system"];

    private static readonly HashSet<string> s_schemaVersions = ["case.v1.0"];

    public static string ToOperation(string operation) =>
        operation switch
        {
            "insert" => CreateOperation,
            UpdateOperation => UpdateOperation,
            _ => throw new InvalidOperationException($"Unregistered analytics operation '{operation}'."),
        };

    public static void Validate(AnalyticsEvent analyticsEvent)
    {
        if (analyticsEvent.Entity != CaseEntity)
            throw new InvalidOperationException($"Unregistered analytics entity '{analyticsEvent.Entity}'.");

        if (!s_eventTypes.Contains(analyticsEvent.EventType))
            throw new InvalidOperationException($"Unregistered analytics event type '{analyticsEvent.EventType}'.");

        var separatorIndex = analyticsEvent.Actor.IndexOf(':');
        if (
            separatorIndex <= 0
            || separatorIndex == analyticsEvent.Actor.Length - 1
            || !s_actorPrefixes.Contains(analyticsEvent.Actor[..separatorIndex])
        )
            throw new InvalidOperationException($"Unregistered analytics actor '{analyticsEvent.Actor}'.");

        if (!s_schemaVersions.Contains(analyticsEvent.SchemaVersion))
            throw new InvalidOperationException(
                $"Unregistered analytics schema version '{analyticsEvent.SchemaVersion}'."
            );
    }

    public static AnalyticsEvent ToAnalyticsEvent(this AuditEvent auditEvent)
    {
        var analyticsEvent = new AnalyticsEvent
        {
            EventId = auditEvent.EventId,
            Sequence = auditEvent.Sequence,
            Entity = auditEvent.Entity,
            EntityId = auditEvent.EntityId,
            Operation = ToOperation(auditEvent.Operation),
            EventType = auditEvent.EventType,
            OccurredAt = auditEvent.OccurredAt,
            RecordedAt = auditEvent.RecordedAt,
            Actor = auditEvent.Actor,
            Version = auditEvent.Version,
            Before = auditEvent.Before,
            After = auditEvent.After,
            SchemaVersion = auditEvent.SchemaVersion,
        };

        Validate(analyticsEvent);

        return analyticsEvent;
    }
}
