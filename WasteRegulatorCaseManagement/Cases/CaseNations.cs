namespace WasteRegulatorCaseManagement.Cases;

/// <summary>The nations a case can belong to.</summary>
public static class CaseNations
{
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        "ENGLAND",
        "WALES",
        "SCOTLAND",
        "N_IRELAND",
    };
}
