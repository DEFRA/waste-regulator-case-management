using System.ComponentModel;
using System.Text.Json.Serialization;
using MongoDB.Bson;

namespace WasteRegulatorCaseManagement.AuditEvents.Analytics;

/// <summary>The message published to the analytics topic for one audit event.</summary>
public sealed record AnalyticsEvent
{
	[JsonPropertyName("eventId")]
	public required string EventId { get; init; }

	[JsonPropertyName("sequence")]
	public long Sequence { get; init; }

	[JsonPropertyName("entity")]
	public required string Entity { get; init; }

	[JsonPropertyName("entityId")]
	public required string EntityId { get; init; }

	[JsonPropertyName("operation")]
	public required string Operation { get; init; }

	[JsonPropertyName("eventType")]
	public required string EventType { get; init; }

	[Description("UTC ISO 8601 format with millisecond precision")]
	[JsonConverter(typeof(UtcMillisecondDateTimeOffsetJsonConverter))]
	[JsonPropertyName("occurredAt")]
	public DateTimeOffset OccurredAt { get; init; }

	[Description("UTC ISO 8601 format with millisecond precision")]
	[JsonConverter(typeof(UtcMillisecondDateTimeOffsetJsonConverter))]
	[JsonPropertyName("recordedAt")]
	public DateTimeOffset RecordedAt { get; init; }

	[JsonPropertyName("actor")]
	public required string Actor { get; init; }

	[JsonPropertyName("version")]
	public int Version { get; init; }

	[JsonConverter(typeof(BsonDocumentJsonConverter))]
	[JsonPropertyName("before")]
	public BsonDocument? Before { get; init; }

	[JsonConverter(typeof(BsonDocumentJsonConverter))]
	[JsonPropertyName("after")]
	public BsonDocument? After { get; init; }

	[JsonPropertyName("schemaVersion")]
	public required string SchemaVersion { get; init; }
}
