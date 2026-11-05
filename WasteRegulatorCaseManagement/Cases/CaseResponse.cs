using System.ComponentModel;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// API representation of a persisted case. Mirrors <see cref="Case"/>
/// and projects engine state (the actions the engine will currently allow) so
/// a UI can render without re-deriving it.
///
/// <see cref="TemplateVersion"/> exposes the version of the type's template
/// the case was assessed against, so a UI can pick a matching detail
/// template for faithful historical rendering.
/// </summary>
[Description("A case, with its timeline and the actions its current state allows.")]
public sealed record CaseResponse(
	[property: Description("Case id, a ULID.")]
	string Id,

	[property: Description("Case type, such as `CSoC`.")]
	string CaseType,

	[property: Description("Current state, one of the case type's states.")]
	string StateId,

	[property: Description("Whether the current state concludes the case.")]
	bool IsTerminal,

	[property: Description("When the submission was received (UTC).")]
	DateTime SubmittedAt,

	[property: Description("When the case last changed (UTC).")]
	DateTime LastModifiedAt,

	[property: Description("Client id of the service that created the case.")]
	string? SubmittedBy,

	[property: Description("Version of the case type configuration the case follows.")]
	string TemplateVersion,

	[property: Description("The submission's id in the source system.")]
	string SourceSystemId,

	[property: Description("Nation the case belongs to: `ENGLAND`, `WALES`, `SCOTLAND` or `N_IRELAND`.")]
	string Nation,

	[property: Description("The submitting organisation's id in the source system.")]
	string OrgId,

	[property: Description("Producer type, as the source system supplied it.")]
	string? ProducerType,

	[property: Description("Fields the caseworker dashboard shows for the case, by name.")]
	IReadOnlyDictionary<string, CaseDisplayField> DisplayData,

	[property: Description("Actions the case's current state allows.")]
	IReadOnlyCollection<CaseTransition> AvailableActions,

	[property: Description("Decision fields recorded by the action that concluded the case, such as `reason`.")]
	IReadOnlyDictionary<string, string>? Decision,

	[property: Description("User id of the assigned caseworker.")]
	string? AssignedToId = null,

	[property: Description("Name of the assigned caseworker.")]
	string? AssignedToName = null,

	[property: Description("When the assignment last changed (UTC).")]
	DateTime? AssignedAt = null,

	[property: Description("User id of the caseworker who last changed the assignment.")]
	string? AssignedBy = null,

	[property: Description("Everything that has happened to the case, newest first.")]
	IReadOnlyCollection<CaseTimelineEntryResponse>? Timeline = null
);

/// <summary>
/// Wire shape for a single timeline entry (RA-97). Returned in
/// reverse-chronological (newest-first) order as part of
/// <see cref="CaseResponse.Timeline"/> so a UI can render a top-to-
/// bottom timeline, most recent first, without re-sorting (RA-568).
/// </summary>
[Description("One thing that happened to a case.")]
public sealed record CaseTimelineEntryResponse(
	[property: Description("Timeline entry id, a ULID.")]
	string Id,

	[property: Description("`case-created`, `state-changed`, `assigned`, `unassigned`, `note-added` or `callback-failed`.")]
	string Action,

	[property: Description("The action as a caseworker reads it, such as `Note added`.")]
	string ActionDisplayName,

	[property: Description("Fields specific to the action, such as `text` for a note or `fromStateId` and `toStateId` for a state change.")]
	IReadOnlyDictionary<string, string?> Details,

	[property: Description("When it happened (UTC).")]
	DateTime PerformedAt,

	[property: Description("User id of the caseworker, or `service:<client id>` for the intake service.")]
	string? PerformedById,

	[property: Description("The caseworker's name at the time, or the client id of the intake service.")]
	string? PerformedByName,

	[property: Description("State of the case after this entry.")]
	string? StateId = null
);
