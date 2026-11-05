namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Declarative description of a case type. Modules implement this to expose
/// their states and the transitions between them. Implementations should
/// be pure, deterministic and side-effect free; they are queried by the framework
/// to advertise what a type can do, not to perform behaviour. Behaviour belongs in
/// the module's service objects.
/// </summary>
public interface ICaseType : ICaseTemplate
{
    /// <summary>Stable, machine-readable identifier (e.g. "CSoC").</summary>
    string CaseType { get; }

    /// <summary>Human-readable name shown in UIs and timelines.</summary>
    string DisplayName { get; }

    /// <summary>The state a newly-ingested case starts in.</summary>
    CaseState InitialState { get; }

    /// <summary>
    /// Stable identifier for the current shape of this type's templates and
    /// Bump this whenever a change to <see cref="States"/> or
    /// <see cref="Transitions"/> would render historical cases
    /// inconsistently. Frontends use the same identifier to pick a matching
    /// detail template, so historical items keep their original look.
    /// Defaults to <c>"v1"</c> so types delivered before versioning existed
    /// continue to compile.
    /// </summary>
    string ICaseTemplate.TemplateVersion => "v1";

    /// <summary>
    /// Allowed state transitions, exposed as named actions (e.g. "approve",
    /// "reject"). The engine consults this list to decide whether an action
    /// invoked by a caller is permitted given the case's current state
    /// Defaults to an empty list so types delivered
    /// before the engine existed continue to compile.
    /// </summary>
    IReadOnlyCollection<CaseTransition> ICaseTemplate.Transitions => Array.Empty<CaseTransition>();
}