namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// The shape the engine actually queries to decide what actions apply to
/// a case. Implemented by both the live <see cref="ICaseType"/> (used
/// for new cases) and by <see cref="CaseTemplateSnapshot"/> (a frozen
/// copy stored on each case when it is submitted, so that audit/history
/// rendering remains faithful even when a module's templates change later).
/// </summary>
public interface ICaseTemplate
{
    /// <summary>Stable, machine-readable identifier of the template version (e.g. "v1").</summary>
    string TemplateVersion { get; }

    /// <summary>All states this template recognises.</summary>
    IReadOnlyCollection<CaseState> States { get; }

    /// <summary>Allowed transitions across <see cref="States"/>.</summary>
    IReadOnlyCollection<CaseTransition> Transitions { get; }
}