using System.Security.Claims;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Module-supplied hook invoked by <see cref="CaseService"/> after a
/// successful submission or action transition. Modules use these to fire
/// side effects (e.g. send a notification, append a module-specific
/// audit entry) without core having to know anything about them.
///
/// Hooks must:
/// <list type="bullet">
///   <item>Be idempotent / safe to invoke once per successful state change.</item>
///   <item>Never throw — failures must be swallowed and recorded by the
///   hook itself (typically via an audit entry written through
///   <see cref="ICaseTimelineAppender"/>) so a side-effect failure does
///   not unwind the originating mutation that has already persisted.</item>
///   <item>Only react to cases whose <see cref="Case.TypeId"/>
///   matches the module they belong to.</item>
/// </list>
/// </summary>
public interface ICasePostActionHook
{
    /// <summary>
    /// Fires after <see cref="ICaseService.SubmitAsync"/> has
    /// successfully created and persisted a new case.
    /// </summary>
    Task OnSubmittedAsync(
        Case caseRecord,
        ClaimsPrincipal user,
        CancellationToken cancellationToken);

    /// <summary>
    /// Fires after <see cref="ICaseService.ApplyActionAsync"/> has
    /// successfully transitioned a case.
    /// <paramref name="actionId"/> is the transition that was just
    /// applied; <paramref name="fromStateId"/> is the state the work
    /// item was in immediately before the transition.
    /// </summary>
    Task OnActionAppliedAsync(
        Case caseRecord,
        string actionId,
        string fromStateId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken);

    /// <summary>
    /// RA-237: fires after <see cref="ICaseService.AssignAsync"/> or
    /// <see cref="ICaseService.UnassignAsync"/> has successfully changed
    /// the assignment of a case. Assignment is a first-class envelope
    /// operation rather than a declared action/transition, so it does not
    /// flow through <see cref="OnActionAppliedAsync"/>; this hook gives
    /// modules the same post-mutation chokepoint for assignment side
    /// effects (e.g. notifying the regulator that an officer has been
    /// assigned).
    ///
    /// Only fires on a real change — an idempotent no-op (re-assigning the
    /// same user, unassigning an already-unassigned item) does not invoke
    /// the hook, mirroring the framework rule that no-ops write no audit
    /// entry. The <see cref="Case"/> passed in reflects the post-change
    /// state, so <see cref="Case.AssignedToId"/> / <c>AssignedToName</c>
    /// / <c>AssignedBy</c> are already the new values (all null on unassign).
    ///
    /// A default no-op implementation is provided so existing hooks that
    /// only care about submission / action transitions are unaffected.
    /// </summary>
    Task OnAssignmentChangedAsync(
        Case caseRecord,
        CaseAssignmentChange change,
        ClaimsPrincipal user,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// RA-237: describes the kind of assignment change that just occurred, so a
/// post-action hook can render the right notification copy without having to
/// re-derive it from before/after assignee ids.
/// </summary>
public enum CaseAssignmentChange
{
    /// <summary>A previously-unassigned item was assigned to an officer.</summary>
    Assigned,

    /// <summary>An already-assigned item was re-assigned to a different officer.</summary>
    Reassigned,

    /// <summary>An assigned item was unassigned.</summary>
    Unassigned
}
