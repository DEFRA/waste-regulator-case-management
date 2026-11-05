using System.ComponentModel;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// A caseworker's note on a case: free text added to the case's timeline
/// that stays there unchanged.
/// </summary>
[Description("A note to add to the case.")]
internal sealed record AddCaseNoteRequest(
	[property: Description("The note, at most 4,000 characters. Leading and trailing whitespace is removed.")]
	string Text
);
