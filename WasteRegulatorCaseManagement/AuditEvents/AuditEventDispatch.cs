using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace WasteRegulatorCaseManagement.AuditEvents;

public sealed record AuditEventDispatch
{
	[BsonRepresentation(BsonType.String)]
	public AuditEventDispatchStatus Status { get; init; }

	[BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
	public DateTime Date { get; init; }

	public string? Message { get; init; }

	public int AttemptCount { get; init; }

	[BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
	[BsonIgnoreIfNull]
	public DateTime? NextAttemptAt { get; init; }
}
