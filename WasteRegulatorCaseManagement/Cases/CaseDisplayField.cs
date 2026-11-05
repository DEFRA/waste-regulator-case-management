using System.ComponentModel;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// One <see cref="Case.DisplayData"/> value with the type a frontend should
/// render it as, one of <see cref="Types"/>.
/// </summary>
[Description("A value for display and how to render it.")]
public sealed record CaseDisplayField(
	[property: Description("The value, as a string.")]
	string Value,

	[property: Description("How to render the value: `string`, `number`, `date` or `boolean`.")]
	string Type
)
{
	public static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.Ordinal)
	{
		"string",
		"number",
		"date",
		"boolean",
	};
}
