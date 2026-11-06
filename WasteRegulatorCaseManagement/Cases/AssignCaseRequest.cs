using System.ComponentModel;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// The caseworker a case is assigned to, as the regulator frontend knows
/// them: their user id and the name shown alongside the case.
/// </summary>
[Description("The caseworker to assign the case to.")]
internal sealed record AssignCaseRequest(
	[property: Description("The caseworker's user id.")]
	string AssigneeId,

	[property: Description("The caseworker's name, kept on the case for display.")]
	string? AssigneeName = null
);
