using MongoDB.Bson;
using NUlid;
using WasteRegulatorCaseManagement.AuditEvents;

namespace WasteRegulatorCaseManagement.Test.TestSupport;

/// <summary>Shared audit-event shapes for completion dispatch tests.</summary>
internal static class AuditEventCompletionTestFixtures
{
	internal static AuditEvent AnalyticsEvent(
		DateTime now,
		long sequence,
		Dictionary<string, AuditEventDispatch>? dispatches = null
	) =>
		new()
		{
			EventId = Ulid.NewUlid().ToString(),
			Sequence = sequence,
			Entity = "case",
			EntityId = $"case_{Ulid.NewUlid()}",
			Operation = "update",
			EventType = "note-added",
			OccurredAt = now.AddMinutes(-10),
			RecordedAt = now.AddMinutes(-10),
			Actor = "user:caseworker-1",
			Version = 2,
			Before = new BsonDocument("version", 1),
			After = new BsonDocument("version", 2),
			SchemaVersion = "case.v1.0",
			Dispatches = dispatches ?? [],
		};

	internal static AuditEvent CompletionEvent(
		DateTime now,
		long sequence,
		Dictionary<string, AuditEventDispatch>? dispatches = null
	)
	{
		var eventId = Ulid.NewUlid().ToString();
		return AnalyticsEvent(now, sequence, dispatches) with
		{
			EventId = eventId,
			EventType = "state-changed",
			CompletionEnvelope = new CaseCompletionEnvelope
			{
				EventId = eventId,
				CaseId = Ulid.NewUlid().ToString(),
				CaseType = "CSoC",
				SourceSystemId = Ulid.NewUlid().ToString(),
				OrgId = "org-1",
				StateId = "ACCEPTED",
				OccurredAt = now.AddMinutes(-10),
				EventType = "csoc.case.concluded",
				Payload = BsonDocument.Parse("""{"status":"Accepted","reason":null}"""),
			},
		};
	}
}