using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Template supplied at intake for the completion message published when a
/// case reaches a terminal state. <see cref="PayloadTemplate"/> is rendered
/// at conclusion time; <see cref="EventType"/> is emitted on the SNS envelope
/// and as a message attribute.
/// </summary>
public sealed class CompletionEventSpec
{
	public required string EventType { get; init; }

	/// <summary>
	/// Outcome body template. String values may contain
	/// <c>{{actor.*}}</c>, <c>{{decision.*}}</c> and <c>{{state.*}}</c>
	/// placeholders.
	/// </summary>
	public required BsonDocument PayloadTemplate { get; init; }
}