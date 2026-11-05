namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Helpers for working with terminal case states. Terminality is derived
/// from a template's declared states rather than hardcoded.
/// </summary>
internal static class TerminalStates
{
    /// <summary>
    /// The terminal state <paramref name="stateId"/> refers to, or
    /// <see langword="null"/> when the state is unknown to
    /// <paramref name="template"/> or is not terminal.
    /// </summary>
    /// <remarks>
    /// Engine operations resolve terminality against the case's own
    /// template (its snapshot where it has one) rather than against
    /// <see cref="Ids"/>, so an in-flight item is never re-judged under a
    /// newer template version. This and <see cref="Ids"/> read the same
    /// <see cref="CaseState.IsTerminal"/> metadata — there is no second,
    /// hardcoded list of "closed" states anywhere in the engine.
    /// </remarks>
    /// <remarks>
    /// Takes a resolved template rather than a nullable one on purpose: an
    /// unresolvable template means terminality is <em>unknown</em>, which is
    /// not the same as "not terminal" and must not collapse into it. Callers
    /// decide what to do about that before they get here — see
    /// <c>CaseService.RequireNonTerminalState</c>, which fails closed.
    /// </remarks>
    internal static CaseState? Find(ICaseTemplate template, string stateId)
    {
        ArgumentNullException.ThrowIfNull(template);

        var state = template.States.FirstOrDefault(s =>
            string.Equals(s.Id, stateId, StringComparison.OrdinalIgnoreCase)
        );

        return state?.IsTerminal == true ? state : null;
    }
}
