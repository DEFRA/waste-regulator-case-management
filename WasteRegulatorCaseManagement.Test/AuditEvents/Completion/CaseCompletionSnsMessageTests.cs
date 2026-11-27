using MongoDB.Bson;
using NUlid;
using WasteRegulatorCaseManagement.AuditEvents;
using WasteRegulatorCaseManagement.AuditEvents.Completion;

namespace WasteRegulatorCaseManagement.Test.AuditEvents.Completion;

public sealed class CaseCompletionSnsMessageTests
{
	[Fact]
	public void FromEnvelope_maps_every_field_to_the_wire_shape()
	{
		var occurredAt = new DateTime(2026, 11, 26, 12, 34, 56, DateTimeKind.Utc);
		var envelope = new CaseCompletionEnvelope
		{
			EventId = Ulid.NewUlid().ToString(),
			CaseId = Ulid.NewUlid().ToString(),
			CaseType = "CSoC",
			SourceSystemId = Ulid.NewUlid().ToString(),
			OrgId = "org-9",
			StateId = "ACCEPTED",
			OccurredAt = occurredAt,
			EventType = "csoc.case.concluded",
			Payload = BsonDocument.Parse("""{"status":"Accepted"}"""),
		};

		var message = CaseCompletionSnsMessage.FromEnvelope(envelope);

		message.Should().BeEquivalentTo(
			new CaseCompletionSnsMessage
			{
				EventId = envelope.EventId,
				CaseId = envelope.CaseId,
				CaseType = envelope.CaseType,
				SourceSystemId = envelope.SourceSystemId,
				OrgId = envelope.OrgId,
				StateId = envelope.StateId,
				OccurredAt = new DateTimeOffset(occurredAt, TimeSpan.Zero),
				EventType = envelope.EventType,
				Payload = envelope.Payload,
			}
		);
	}
}