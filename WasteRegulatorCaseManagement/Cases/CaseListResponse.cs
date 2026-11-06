using System.ComponentModel;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// API representation of a single page of cases returned by
/// <c>GET /cases</c>. Mirrors <see cref="CasePage"/> but carries
/// API-shaped <see cref="CaseListItemResponse"/>s — a slimmer
/// per-item shape than <see cref="CaseResponse"/> that omits the
/// per-item <c>Timeline</c>. A 100-row page of an item with
/// chatty caseworker activity would otherwise ship the entire history for
/// every row even though
/// the list view never displays them; the slim shape keeps the wire
/// payload bounded by the envelope size.
///
/// Callers that need the full timeline must hit
/// <c>GET /cases/{id}</c>, which still returns the unmodified
/// <see cref="CaseResponse"/>.
/// </summary>
[Description("A page of cases.")]
public sealed record CaseListResponse(
	[property: Description("The cases on this page.")]
	IReadOnlyList<CaseListItemResponse> Items,

	[property: Description("Number of cases matching the filters, across all pages.")]
	long TotalCount,

	[property: Description("This page number, from 1.")]
	int Page,

	[property: Description("Maximum number of cases on a page.")]
	int PageSize
);

/// <summary>
/// Per-item shape returned by the list endpoint. Mirrors
/// <see cref="CaseResponse"/> exactly except that the
/// <c>Timeline</c> is omitted entirely (the JSON property does not exist on the wire — they are not
/// emitted-as-null) so list responses stay small. Every other field —
/// available actions, assignment snapshot, template version — is kept so
/// a list view can render rich rows without a per-row round-trip.
/// </summary>
[Description("A case in a list, without its timeline.")]
public sealed record CaseListItemResponse(
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

	[property: Description("User id of the assigned caseworker.")]
	string? AssignedToId = null,

	[property: Description("Name of the assigned caseworker.")]
	string? AssignedToName = null,

	[property: Description("When the assignment last changed (UTC).")]
	DateTime? AssignedAt = null,

	[property: Description("User id of the caseworker who last changed the assignment.")]
	string? AssignedBy = null
);
