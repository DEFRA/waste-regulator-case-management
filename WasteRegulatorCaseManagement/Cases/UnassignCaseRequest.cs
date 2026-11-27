using System.ComponentModel;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// The caseworker removing a case's assignee, as the regulator frontend
/// knows them.
/// </summary>
[Description("The caseworker unassigning the case.")]
internal sealed record UnassignCaseRequest(
	[property: Description("The name of the caseworker unassigning the case, recorded on the timeline.")]
	string? ActorName = null
);
