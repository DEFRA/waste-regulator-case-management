using WasteRegulatorCaseManagement.Cases;

namespace WasteRegulatorCaseManagement.Test.Cases;

/// <summary>
/// Builds a minimal in-test case type used to exercise the engine.
/// </summary>
internal static class TestCaseType
{
    public static CaseModuleConfig Create(
        string caseType,
        string displayName,
        CaseState? initialState = null,
        IReadOnlyCollection<CaseState>? states = null,
        IReadOnlyCollection<CaseTransition>? transitions = null)
    {
        var initial = initialState ?? new CaseState("submitted", "Submitted");
        return new CaseModuleConfig
        {
            CaseType = caseType,
            DisplayName = displayName,
            TemplateVersion = "v1",
            InitialStateId = initial.Id,
            States = states ?? [initial],
            Transitions = transitions ?? [],
        };
    }
}
