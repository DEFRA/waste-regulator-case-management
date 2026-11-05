namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// One <see cref="Case.DisplayData"/> value with the type a frontend should
/// render it as, one of <see cref="Types"/>.
/// </summary>
public sealed record CaseDisplayField(string Value, string Type)
{
    public static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.Ordinal)
    {
        "string",
        "number",
        "date",
        "boolean",
    };
}
