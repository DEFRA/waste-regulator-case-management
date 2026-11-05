using MongoDB.Bson.Serialization.Attributes;

namespace WasteRegulatorCaseManagement.AuditEvents;

[BsonIgnoreExtraElements]
public sealed record AuditEventDispatchLease
{
	[BsonId]
	public required string Id { get; init; }

	public string? Owner { get; init; }

	[BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
	public DateTime ExpiresAt { get; init; }

	[BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
	public DateTime CreatedAt { get; init; }

	[BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
	public DateTime UpdatedAt { get; init; }

	[BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
	public DateTime? LastSentAt { get; init; }
}
