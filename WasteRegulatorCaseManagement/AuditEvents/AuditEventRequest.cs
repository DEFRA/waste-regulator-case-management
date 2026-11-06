using MongoDB.Bson;

namespace WasteRegulatorCaseManagement.AuditEvents;

public sealed record AuditEventRequest(
	string Actor,
	string Entity,
	AuditEventOperation Operation,
	string EventType,
	string EntityId,
	int Version,
	BsonDocument? Before,
	BsonDocument? After,
	string SchemaVersion,
	DateTime OccurredAt
);
