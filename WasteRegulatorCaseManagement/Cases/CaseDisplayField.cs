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

	/// <summary>
	/// Whether <paramref name="name"/> can name a display field: one or more
	/// ASCII letters, digits, <c>_</c> or <c>-</c>. The name becomes part of a
	/// Mongo field path when the case list filters on it, so it must not hold
	/// a <c>.</c>, a <c>$</c> or anything else Mongo would read as syntax.
	/// </summary>
	public static bool IsValidName(string? name) =>
		!string.IsNullOrEmpty(name) && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-');

	private const int MaxEchoedLength = 100;

	/// <summary>
	/// <paramref name="text"/> cut to 100 characters, for repeating a
	/// rejected name or filter back to the caller in an error message.
	/// </summary>
	internal static string Shortened(string text) =>
		text.Length <= MaxEchoedLength ? text : text[..MaxEchoedLength] + "…";
}
