using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// A case type's state machine, stored in the <c>caseModuleConfig</c>
/// collection with <see cref="CaseType"/> as its id. The engine validates
/// every intake and action against it.
/// </summary>
[BsonIgnoreExtraElements]
public sealed class CaseModuleConfig : ICaseTemplate
{
	/// <summary>Stable, machine-readable identifier (e.g. "CSoC") that intake names in <c>caseType</c>.</summary>
	[BsonId]
	public required string CaseType { get; init; }

	/// <summary>Human-readable name shown in UIs and timelines.</summary>
	public required string DisplayName { get; init; }

	/// <summary>
	/// Identifies this shape of <see cref="States"/> and
	/// <see cref="Transitions"/>. Change it whenever a change would render
	/// existing cases inconsistently; each case keeps the snapshot it was
	/// submitted under.
	/// </summary>
	public required string TemplateVersion { get; init; }

	/// <summary>The <see cref="CaseState.Id"/> a newly-ingested case starts in.</summary>
	public required string InitialStateId { get; init; }

	public required IReadOnlyCollection<CaseState> States { get; init; }

	/// <summary>
	/// Allowed state transitions, exposed as named actions (e.g. "accept",
	/// "cancel").
	/// </summary>
	public required IReadOnlyCollection<CaseTransition> Transitions { get; init; }

	/// <summary>
	/// The payload PATCHed to a case's <see cref="Case.CallbackUrl"/> when
	/// it moves into a state, keyed by state id. String values may contain
	/// <c>{{actor.id}}</c>, <c>{{actor.email}}</c>, <c>{{actor.name}}</c> and
	/// <c>{{decision.field}}</c> placeholders.
	/// </summary>
	[JsonConverter(typeof(CallbackMappingsJsonConverter))]
	public Dictionary<string, BsonDocument> CallbackMappings { get; init; } = [];

	[BsonIgnore]
	[JsonIgnore]
	public CaseState InitialState =>
		States.First(s => string.Equals(s.Id, InitialStateId, StringComparison.OrdinalIgnoreCase));
}
