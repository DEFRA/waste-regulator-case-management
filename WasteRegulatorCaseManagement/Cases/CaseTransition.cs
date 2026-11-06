using System.ComponentModel;
using MongoDB.Bson.Serialization.Attributes;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Declares an allowed move between two <see cref="CaseState"/>s, exposed
/// as a named action (e.g. "accept", "cancel"). Declared on a
/// <see cref="CaseModuleConfig"/> so the engine can decide which actions a
/// caller may invoke for a case in its current state.
///
/// Embedded verbatim in a frozen <see cref="CaseTemplateSnapshot"/>, so it
/// ignores extra BSON elements: a snapshot persisted under an older template
/// with since-removed fields must still deserialise when the worklist is queried rather than throwing a
/// <see cref="System.FormatException"/> for the whole batch.
/// </summary>
/// <param name="ActionId">Stable, machine-readable id of the action (e.g. "accept").</param>
/// <param name="DisplayName">Human-readable label shown in UIs and timelines.</param>
/// <param name="FromStateId">State the case must be in for the action to be allowed.</param>
/// <param name="ToStateId">State the case moves to when the action succeeds.</param>
/// <param name="RequiredDecisionFields">
/// Fields the caller's <c>decision</c> must supply, non-blank, for the
/// action to be applied (e.g. CSoC <c>cancel</c> requires <c>reason</c>).
/// Absent for most transitions.
/// </param>
[BsonIgnoreExtraElements]
[Description("An action that moves a case from one state to another.")]
public sealed record CaseTransition(
	[property: Description("The action's id, used in `POST /cases/{id}/actions/{actionId}`.")]
	string ActionId,

	[property: Description("The action as a caseworker reads it, such as `Accept`.")]
	string DisplayName,

	[property: Description("State the case must be in.")]
	string FromStateId,

	[property: Description("State the case moves to.")]
	string ToStateId,

	[property: Description("Fields the action's `decision` must supply, such as `reason`.")]
	IReadOnlyCollection<string>? RequiredDecisionFields = null
);
