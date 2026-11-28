using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace WasteRegulatorCaseManagement.AuditEvents;

/// <summary>
/// The completion message written to the outbox and published to SNS when a
/// case reaches a terminal state. <see cref="EventId"/> matches the parent
/// <see cref="AuditEvent.EventId"/> so consumers can deduplicate.
/// </summary>
public sealed record CaseCompletionEnvelope
{
	public required string EventId { get; init; }
	public required string CaseId { get; init; }
	public required string CaseType { get; init; }
	public required string SourceSystemId { get; init; }
	public required string OrgId { get; init; }
	public required string StateId { get; init; }

	[BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
	public required DateTime OccurredAt { get; init; }

	/// <summary>SNS message attribute and routing id (e.g. <c>csoc.case.concluded</c>).</summary>
	public required string EventType { get; init; }

	public required BsonDocument Payload { get; init; }
}

/// <summary>Fields to attach to an audit event before <see cref="EventId"/> is allocated.</summary>
public sealed record CaseCompletionEnvelopeContent(
	string CaseId,
	string CaseType,
	string SourceSystemId,
	string OrgId,
	string StateId,
	DateTime OccurredAt,
	string EventType,
	BsonDocument Payload
);