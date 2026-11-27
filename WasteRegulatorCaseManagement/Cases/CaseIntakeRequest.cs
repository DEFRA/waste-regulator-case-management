using System.ComponentModel;
using System.Text.Json;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// A submission from a source system that needs a regulator's decision:
/// which nation and organisation it belongs to, what completion event to
/// publish when it concludes, and what the caseworker dashboard shows.
/// </summary>
[Description("A submission to create a case for.")]
internal sealed record CaseIntakeRequest(
	[property: Description("Case type to create, such as `CSoC`.")]
	string CaseType,

	[property: Description("The submission's id in the source system. One case is created per submission.")]
	string SourceSystemId,

	[property: Description("Template for the completion event published when the case concludes.")]
	CompletionEventIntakeRequest? CompletionEvent,

	[property: Description("Nation the case belongs to: `ENGLAND`, `WALES`, `SCOTLAND` or `N_IRELAND`.")]
	string Nation,

	[property: Description("The submitting organisation's id in the source system.")]
	string OrgId,

	[property: Description("Producer type, stored and returned unchanged. For CSoC, `DIRECT_PRODUCER` or `COMPLIANCE_SCHEME`.")]
	string? ProducerType = null,

	[property: Description("Fields the caseworker dashboard shows for the case, by name.")]
	IReadOnlyDictionary<string, CaseDisplayField>? DisplayData = null,

	[property: Description("Bulk load only: the state of the case type to start the case in, instead of its initial state.")]
	string? InitialState = null,

	[property: Description("Bulk load only: the original submission time, kept as `submittedAt`. A time without an offset is taken as UTC.")]
	DateTime? CreatedAt = null,

	[property: Description("Name of the submitting system, recorded on the `case-created` timeline entry.")]
	string? Source = null
);

[Description("Completion event template supplied at intake.")]
internal sealed record CompletionEventIntakeRequest(
	[property: Description("Event type for SNS routing (e.g. `csoc.case.concluded`).")]
	string EventType,

	[property: Description("Payload template rendered at conclusion time.")]
	JsonElement Payload
);