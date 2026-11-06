using System.ComponentModel;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// A caseworker's decision on a case, given with the action that concludes
/// it, such as the reason a CSoC submission is cancelled.
/// </summary>
[Description("The decision recorded with the action.")]
internal sealed record CaseActionRequest(
	[property: Description("Decision fields, by name. An action that requires fields, such as CSoC `cancel` and its `reason`, is refused without them.")]
	IReadOnlyDictionary<string, string>? Decision = null
);
