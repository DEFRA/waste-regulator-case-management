using WasteRegulatorCaseManagement.Cases;

namespace WasteRegulatorCaseManagement.Test.Cases;

/// <summary>
/// Minimal in-test case type used to exercise the framework. Real types
/// live in their own modules under <c>WasteRegulatorCaseManagement/Cases/&lt;Type&gt;</c>.
/// </summary>
internal sealed class TestCaseType : ICaseType
{
    public TestCaseType(
        string caseType,
        string displayName,
        CaseState? initialState = null,
        IReadOnlyCollection<CaseState>? states = null,
        IReadOnlyCollection<CaseTransition>? transitions = null)
    {
        CaseType = caseType;
        DisplayName = displayName;
        InitialState = initialState ?? new CaseState("submitted", "Submitted");
        States = states ?? [InitialState];
        Transitions = transitions ?? Array.Empty<CaseTransition>();
    }

    public string CaseType { get; }
    public string DisplayName { get; }
    public CaseState InitialState { get; }
    public IReadOnlyCollection<CaseState> States { get; }
    public IReadOnlyCollection<CaseTransition> Transitions { get; }
}