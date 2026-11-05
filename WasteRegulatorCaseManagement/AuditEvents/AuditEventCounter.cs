using MongoDB.Bson.Serialization.Attributes;

namespace WasteRegulatorCaseManagement.AuditEvents;

[BsonIgnoreExtraElements]
public sealed record AuditEventCounter
{
	[BsonId]
	public required string Id { get; init; }

	public long Sequence { get; init; }
}
