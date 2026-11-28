using System.ComponentModel;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using WasteRegulatorCaseManagement.AuditEvents.Analytics;

namespace WasteRegulatorCaseManagement.AuditEvents.Completion;

/// <summary>Wire shape published to the case completion SNS topic.</summary>
public sealed record CaseCompletionSnsMessage
{
	[JsonPropertyName("eventId")]
	public required string EventId { get; init; }

	[JsonPropertyName("caseId")]
	public required string CaseId { get; init; }

	[JsonPropertyName("caseType")]
	public required string CaseType { get; init; }

	[JsonPropertyName("sourceSystemId")]
	public required string SourceSystemId { get; init; }

	[JsonPropertyName("orgId")]
	public required string OrgId { get; init; }

	[JsonPropertyName("stateId")]
	public required string StateId { get; init; }

	[Description("UTC ISO 8601 format with millisecond precision")]
	[JsonConverter(typeof(UtcMillisecondDateTimeOffsetJsonConverter))]
	[JsonPropertyName("occurredAt")]
	public DateTimeOffset OccurredAt { get; init; }

	[JsonPropertyName("eventType")]
	public required string EventType { get; init; }

	[JsonConverter(typeof(BsonDocumentJsonConverter))]
	[JsonPropertyName("payload")]
	public required BsonDocument Payload { get; init; }

	public static CaseCompletionSnsMessage FromEnvelope(CaseCompletionEnvelope envelope) =>
		new()
		{
			EventId = envelope.EventId,
			CaseId = envelope.CaseId,
			CaseType = envelope.CaseType,
			SourceSystemId = envelope.SourceSystemId,
			OrgId = envelope.OrgId,
			StateId = envelope.StateId,
			OccurredAt = new DateTimeOffset(envelope.OccurredAt, TimeSpan.Zero),
			EventType = envelope.EventType,
			Payload = envelope.Payload,
		};
}