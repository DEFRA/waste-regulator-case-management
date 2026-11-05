using System.Security.Claims;
using MongoDB.Bson;

namespace WasteRegulatorCaseManagement.WorkItems.Core;

/// <summary>
/// Framework service object that drives state transitions for every work
/// item type. Lives in core because the rules ("you cannot act on a closed
/// case", "you cannot invoke an action
/// that does not apply to the current state", etc.) are universal across
/// modules. Module-specific business logic belongs in module service objects
/// that may be called before/after this engine.
/// </summary>
public interface IWorkItemService
{
    /// <summary>
    /// Create a brand-new work item of <paramref name="type"/> and persist
    /// it. Captures a frozen template snapshot, stamps a server-side
    /// submission timestamp from the injected <see cref="TimeProvider"/>,
    /// and appends a single <c>work-item-submitted</c> entry to the new
    /// work item's audit log so the audit timeline starts at the
    /// document's birth event rather than the first state change. The
    /// audit entry and the document body are written in the same
    /// <see cref="IWorkItemPersistence.CreateAsync"/> call. Mutations
    /// require a <c>user:id</c> claim — calls without one return
    /// <see cref="WorkItemActionFailureCode.MissingActorIdentity"/> and
    /// nothing is persisted.
    /// </summary>
    Task<WorkItemActionResult> SubmitAsync(
        IWorkItemType type,
        BsonDocument payload,
        string? submittedBy,
        ClaimsPrincipal user,
        IReadOnlyDictionary<string, string?>? submissionMetadata = null,
        CancellationToken cancellationToken = default
    );

    Task<WorkItemActionResult> ApplyActionAsync(
        Guid workItemId,
        string actionId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Assign (or re-assign) a work item to <paramref name="assigneeId"/>,
    /// snapshotting <paramref name="assigneeName"/> alongside the id so list
    /// views do not need a separate user lookup. RA-323: every caseworker
    /// may assign to anyone, including re-assigning an already-assigned
    /// item — there is no separate permission tier.
    /// </summary>
    Task<WorkItemActionResult> AssignAsync(
        Guid workItemId,
        string assigneeId,
        string? assigneeName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Clear the current assignment. Any authenticated caseworker may
    /// unassign (RA-323).
    /// </summary>
    Task<WorkItemActionResult> UnassignAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Append a free-text work-item-level note (RA-96). Authoring identity is
    /// snapshotted from the supplied <see cref="ClaimsPrincipal"/> at the
    /// time of the call so the audit narrative survives later directory
    /// changes. Notes are append-only; this is the only mutation the
    /// framework offers for them. Audited as <c>note-added</c>. The
    /// withdraw and decision-rationale flows are the only current callers —
    /// the standalone FE "add a note" feature and task-scoped notes have
    /// been removed.
    /// </summary>
    Task<WorkItemActionResult> AddNoteAsync(
        Guid workItemId,
        string text,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Compute the currently-available actions for a work item. Returns
    /// <c>null</c> when no work item exists with the supplied id.
    /// </summary>
    Task<WorkItemEngineProjection?> ProjectAsync(
        Guid workItemId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Project an already-loaded work item. Pure; safe to call without I/O.</summary>
    WorkItemEngineProjection Project(WorkItem workItem);
}

/// <summary>Snapshot of a work item alongside its engine-derived view.</summary>
public sealed record WorkItemEngineProjection(
    WorkItem WorkItem,
    string TemplateVersion,
    IReadOnlyCollection<WorkItemTransition> AvailableActions
);

public sealed class WorkItemService : IWorkItemService
{
    private const string SourceKey = "source";

    private readonly IWorkItemRegistry _registry;
    private readonly IWorkItemPersistence _persistence;
    private readonly ILogger<WorkItemService> _logger;
    private readonly IReadOnlyCollection<IWorkItemPostActionHook> _postActionHooks;
    private readonly TimeProvider _timeProvider;

    public WorkItemService(
        IWorkItemRegistry registry,
        IWorkItemPersistence persistence,
        ILogger<WorkItemService> logger,
        TimeProvider? timeProvider = null,
        IEnumerable<IWorkItemPostActionHook>? postActionHooks = null
    )
    {
        this._registry = registry;
        this._persistence = persistence;
        this._logger = logger;
        _postActionHooks = postActionHooks?.ToArray() ?? Array.Empty<IWorkItemPostActionHook>();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<WorkItemActionResult> SubmitAsync(
        IWorkItemType type,
        BsonDocument payload,
        string? submittedBy,
        ClaimsPrincipal user,
        IReadOnlyDictionary<string, string?>? submissionMetadata = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(payload);

        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // RA-196: ensure 'source' is in the payload. If the caller provided
        // it in the submission metadata (the standard BFF pattern) it is
        // mirrored here so consumers can find it without parsing the audit
        // log.
        if (
            submissionMetadata != null
            && submissionMetadata.TryGetValue(SourceKey, out var sourceVal)
            && sourceVal != null
            && !payload.Contains(SourceKey)
        )
        {
            payload[SourceKey] = sourceVal;
        }

        var snapshot = WorkItemTemplateSnapshot.Capture(type);

        var workItem = new WorkItem
        {
            TypeId = type.TypeId,
            StateId = type.InitialState.Id,
            SubmittedAt = now,
            LastModifiedAt = now,
            SubmittedBy = submittedBy,
            TemplateSnapshot = snapshot,
            TemplateVersion = snapshot.TemplateVersion,
            Payload = payload,
        };

        // Birth event: the audit timeline must start at submission rather
        // than the first state change. Appended to the in-memory list
        // before the single CreateAsync call so the document and its first
        // audit entry land in storage together.
        //
        // RA-126: enrich the birth entry with the originating BFF /
        // application context so audit consumers can reconstruct who
        // submitted from where without joining back to request logs.
        // 'source' is caller-supplied (the BFF forwards it);
        // 'clientId' is the caller-asserted, HMAC-verified client id
        // (see ClientIdAuthenticationHandler); 'userId' is the end-user
        // identity claim required for any mutation.
        AppendAudit(
            workItem,
            "work-item-submitted",
            "Work item submitted",
            user,
            now,
            new()
            {
                ["typeId"] = type.TypeId,
                ["stateId"] = workItem.StateId,
                ["templateVersion"] = snapshot.TemplateVersion,
                [SourceKey] =
                    submissionMetadata is not null
                    && submissionMetadata.TryGetValue(SourceKey, out var src)
                        ? src
                        : null,
                ["clientId"] = user.FindFirstValue("client_id"),
                ["userId"] = ResolveActorUserId(user),
            }
        );

        try
        {
            await _persistence.CreateAsync(workItem, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "MongoDB write failed for work item {WorkItemId} typeId={TypeId} submittedBy={SubmittedBy}: {ExceptionType}",
                workItem.Id,
                type.TypeId,
                submittedBy,
                ex.GetType().Name
            );
            throw;
        }

        _logger.LogInformation(
            "Work item {WorkItemId} ({TypeId}) submitted in state {StateId} by {User}",
            workItem.Id,
            workItem.TypeId,
            workItem.StateId,
            DescribeUser(user)
        );

        await InvokeSubmittedHooksAsync(workItem, user, cancellationToken);

        return WorkItemActionResult.Success(workItem);
    }

    public async Task<WorkItemActionResult> ApplyActionAsync(
        Guid workItemId,
        string actionId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
    {
        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        var (workItem, template, failure) = await LoadAsync(workItemId, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var actionTransitions = template!
            .Transitions.Where(t =>
                string.Equals(t.ActionId, actionId, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
        if (actionTransitions.Count == 0)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.UnknownAction,
                $"Action '{actionId}' is not declared by work item type '{workItem!.TypeId}'."
            );
        }

        // An action id can be declared as a self-loop on more than one state
        // (e.g. `sla-extend` on both `assessment-in-progress` and `queried`),
        // so resolve to the transition whose FromStateId matches the work
        // item's current state — matching on action id alone would always pick
        // the first declaration and wrongly reject the action from every other
        // state it is valid in. Fall back to the first declaration only so the
        // InvalidTransition message below still names a valid from-state.
        var transition =
            actionTransitions.FirstOrDefault(t =>
                string.Equals(t.FromStateId, workItem!.StateId, StringComparison.OrdinalIgnoreCase)
            ) ?? actionTransitions[0];

        if (TerminalStates.Find(template, workItem!.StateId) is { } currentTerminalState)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.TerminalState,
                $"Work item {workItemId} is in terminal state '{currentTerminalState.Id}'; no actions are allowed."
            );
        }

        if (
            !string.Equals(
                transition.FromStateId,
                workItem!.StateId,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidTransition,
                $"Action '{actionId}' moves work items from '{transition.FromStateId}', "
                    + $"but {workItemId} is in '{workItem.StateId}'."
            );
        }

        var previousState = workItem.StateId;
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        workItem.StateId = transition.ToStateId;
        workItem.LastModifiedAt = now;
        AppendAudit(
            workItem,
            "action-applied",
            "Action applied",
            user,
            now,
            new()
            {
                ["actionId"] = transition.ActionId,
                ["actionDisplayName"] = transition.DisplayName,
                ["fromStateId"] = previousState,
                ["toStateId"] = workItem.StateId,
            }
        );
        try
        {
            await _persistence.ReplaceAsync(workItem, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            return ConcurrencyConflict(workItem.Id);
        }
        _logger.LogInformation(
            "Work item {WorkItemId} ({TypeId}) transitioned from {FromState} to {ToState} via action {ActionId} by {User}",
            workItem.Id,
            workItem.TypeId,
            previousState,
            workItem.StateId,
            transition.ActionId,
            DescribeUser(user)
        );

        await InvokeActionAppliedHooksAsync(
            workItem,
            transition.ActionId,
            previousState,
            user
        );

        return WorkItemActionResult.Success(workItem);
    }

    public async Task<WorkItemActionResult> AssignAsync(
        Guid workItemId,
        string assigneeId,
        string? assigneeName,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
    {
        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        if (string.IsNullOrWhiteSpace(assigneeId))
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidAssignment,
                "Assignee id is required."
            );
        }

        var workItem = await _persistence.GetByIdAsync(workItemId, cancellationToken);
        if (workItem is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'."
            );
        }

        if (RequireNonTerminalState(workItem, "assigned") is { } terminalFailure)
        {
            return terminalFailure;
        }

        var trimmedAssigneeId = assigneeId.Trim();
        var actorUserId = ResolveActorUserId(user);

        // RA-323: every caseworker may assign to anyone, including
        // re-assigning an already-assigned item — there is no permission
        // tier to check here any more.

        var snapshotName = string.IsNullOrWhiteSpace(assigneeName) ? null : assigneeName.Trim();
        var alreadyAssignedToSameUser =
            string.Equals(workItem.AssignedToId, trimmedAssigneeId, StringComparison.Ordinal)
            && string.Equals(workItem.AssignedToName, snapshotName, StringComparison.Ordinal);

        if (alreadyAssignedToSameUser)
        {
            // Re-assigning to the same user is a no-op: persist nothing,
            // write no audit entry, but tell the caller this was a replay
            // (parity with CompleteTaskAsync) so the endpoint can surface
            // X-Idempotent-Replay: true and the UI can render an
            // appropriate state instead of a misleading "newly assigned".
            return WorkItemActionResult.IdempotentReplay(workItem);
        }

        var previousAssigneeId = workItem.AssignedToId;
        var previousAssigneeName = workItem.AssignedToName;
        workItem.AssignedToId = trimmedAssigneeId;
        workItem.AssignedToName = snapshotName;
        workItem.AssignedAt = _timeProvider.GetUtcNow().UtcDateTime;
        workItem.AssignedBy = actorUserId!;
        workItem.LastModifiedAt = workItem.AssignedAt.Value;
        AppendAudit(
            workItem,
            "assigned",
            "Assigned",
            user,
            workItem.AssignedAt.Value,
            new()
            {
                ["assigneeId"] = trimmedAssigneeId,
                ["assigneeName"] = snapshotName,
                ["previousAssigneeId"] = previousAssigneeId,
                ["previousAssigneeName"] = previousAssigneeName,
            }
        );
        try
        {
            await _persistence.ReplaceAsync(workItem, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            return ConcurrencyConflict(workItem.Id);
        }
        _logger.LogInformation(
            "Work item {WorkItemId} ({TypeId}) assigned from {PreviousAssignee} to {NewAssignee} by {User}",
            workItem.Id,
            workItem.TypeId,
            previousAssigneeId ?? "(unassigned)",
            trimmedAssigneeId,
            DescribeUser(user)
        );

        // RA-237: assignment is a first-class envelope operation, so it must
        // fan out to the post-action hooks explicitly (ApplyActionAsync's hook
        // fan-out does not cover it). A brand-new assignment on a previously
        // unassigned item is Assigned; changing the assignee of an
        // already-assigned item is Reassigned. Fires only on this real-change
        // path — the earlier IdempotentReplay return skips it.
        var change = previousAssigneeId is null
            ? WorkItemAssignmentChange.Assigned
            : WorkItemAssignmentChange.Reassigned;
        await InvokeAssignmentChangedHooksAsync(workItem, change, user, cancellationToken);

        return WorkItemActionResult.Success(workItem);
    }

    public async Task<WorkItemActionResult> UnassignAsync(
        Guid workItemId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
    {
        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        var workItem = await _persistence.GetByIdAsync(workItemId, cancellationToken);
        if (workItem is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'."
            );
        }

        if (RequireNonTerminalState(workItem, "unassigned") is { } terminalFailure)
        {
            return terminalFailure;
        }

        if (workItem.AssignedToId is null)
        {
            // Idempotent: already unassigned, nothing to do. Flag as a
            // replay (parity with CompleteTaskAsync / AssignAsync) so the
            // endpoint can set X-Idempotent-Replay: true.
            return WorkItemActionResult.IdempotentReplay(workItem);
        }

        var previousAssigneeId = workItem.AssignedToId;
        var previousAssigneeName = workItem.AssignedToName;
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        workItem.AssignedToId = null;
        workItem.AssignedToName = null;
        workItem.AssignedAt = null;
        workItem.AssignedBy = null;
        workItem.LastModifiedAt = now;
        AppendAudit(
            workItem,
            "unassigned",
            "Unassigned",
            user,
            now,
            new()
            {
                ["previousAssigneeId"] = previousAssigneeId,
                ["previousAssigneeName"] = previousAssigneeName,
            }
        );
        try
        {
            await _persistence.ReplaceAsync(workItem, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            return ConcurrencyConflict(workItem.Id);
        }
        _logger.LogInformation(
            "Work item {WorkItemId} ({TypeId}) unassigned (was {PreviousAssignee}) by {User}",
            workItem.Id,
            workItem.TypeId,
            previousAssigneeId,
            DescribeUser(user)
        );

        // RA-237: notify the post-action hooks of the real unassignment.
        // The earlier IdempotentReplay return (already unassigned) skips this.
        await InvokeAssignmentChangedHooksAsync(
            workItem,
            WorkItemAssignmentChange.Unassigned,
            user,
            cancellationToken
        );

        return WorkItemActionResult.Success(workItem);
    }

    /// <summary>
    /// Maximum length of a single note body. Picked to comfortably hold a
    /// long paragraph of assessor narrative without enabling the field as a
    /// dumping ground for documents. Enforced at the service boundary so
    /// callers see the same limit regardless of transport.
    /// </summary>
    public const int MaxNoteLength = 4000;

    public async Task<WorkItemActionResult> AddNoteAsync(
        Guid workItemId,
        string text,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default
    )
    {
        if (RequireActorIdentity(user) is { } identityFailure)
        {
            return identityFailure;
        }

        if (ValidateNoteText(text, out var trimmed, out var noteFailure) is false)
        {
            return noteFailure!;
        }

        // Work-item-level note (RA-96): a single GetByIdAsync, no template
        // resolution required, so the note still works for legacy items
        // whose module is no longer registered and which carry no
        // template snapshot.
        var workItem = await _persistence.GetByIdAsync(workItemId, cancellationToken);
        if (workItem is null)
        {
            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.WorkItemNotFound,
                $"No work item exists with id '{workItemId}'."
            );
        }

        var note = new WorkItemNote
        {
            Text = trimmed!,
            CreatedAt = _timeProvider.GetUtcNow().UtcDateTime,
            CreatedBy = ResolveActorUserId(user)!,
            CreatedByName = user?.FindFirstValue("user:name"),
        };
        workItem.Notes.Add(note);
        workItem.LastModifiedAt = note.CreatedAt;

        AppendAudit(
            workItem,
            "note-added",
            "Note added",
            user,
            note.CreatedAt,
            new()
            {
                ["noteId"] = note.Id.ToString(),
                // Snapshot the trimmed body so the audit log is self-describing —
                // a reader does not need to cross-reference Notes by id to see
                // what was written. Already capped by MaxNoteLength.
                ["noteText"] = note.Text,
            }
        );

        try
        {
            await _persistence.ReplaceAsync(workItem, cancellationToken);
        }
        catch (WorkItemConcurrencyException)
        {
            return ConcurrencyConflict(workItem.Id);
        }
        _logger.LogInformation(
            "Note {NoteId} added to work item {WorkItemId} ({TypeId}) by {User}",
            note.Id,
            workItem.Id,
            workItem.TypeId,
            DescribeUser(user)
        );

        return WorkItemActionResult.Success(workItem);
    }

    private static bool ValidateNoteText(
        string text,
        out string? trimmed,
        out WorkItemActionResult? failure
    )
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            trimmed = null;
            failure = WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidNote,
                "Note text is required."
            );
            return false;
        }

        var t = text.Trim();
        if (t.Length > MaxNoteLength)
        {
            trimmed = null;
            failure = WorkItemActionResult.Failure(
                WorkItemActionFailureCode.InvalidNote,
                $"Note text must be {MaxNoteLength} characters or fewer."
            );
            return false;
        }

        trimmed = t;
        failure = null;
        return true;
    }

    public async Task<WorkItemEngineProjection?> ProjectAsync(
        Guid workItemId,
        CancellationToken cancellationToken = default
    )
    {
        var workItem = await _persistence.GetByIdAsync(workItemId, cancellationToken);
        return workItem is null ? null : Project(workItem);
    }

    public WorkItemEngineProjection Project(WorkItem workItem)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        var template = ResolveTemplate(workItem);
        if (template is null)
        {
            // The work item exists but its module is no longer registered and
            // no snapshot is on file (e.g. a legacy item). Render it as having
            // no available actions so callers can still display it.
            return new WorkItemEngineProjection(
                workItem,
                ResolveTemplateVersion(workItem),
                Array.Empty<WorkItemTransition>()
            );
        }

        var isTerminal = TerminalStates.Find(template, workItem.StateId) is not null;

        IReadOnlyCollection<WorkItemTransition> available = isTerminal
            ? Array.Empty<WorkItemTransition>()
            : template
                .Transitions.Where(t =>
                    string.Equals(
                        t.FromStateId,
                        workItem.StateId,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                // RA-364: never offer an action the generic action endpoint
                // would reject. Transitions declared CallerInvocable: false
                // are reachable only through a module's own bespoke service
                // resolving them server-side, so listing them here would
                // advertise buttons that always fail — and, where several
                // share a DisplayName and a FromStateId, render as identical
                // dead controls. This mirrors the guard in
                // WorkItemEndpoints.Action; the bespoke services are
                // unaffected because they call ApplyActionAsync directly and
                // never consult AvailableActions.
                .Where(t => t.CallerInvocable)
                .ToList();

        return new WorkItemEngineProjection(
            workItem,
            template.TemplateVersion,
            available
        );
    }

    private async Task<(
        WorkItem? WorkItem,
        IWorkItemTemplate? Template,
        WorkItemActionResult? Failure
    )> LoadAsync(Guid workItemId, CancellationToken cancellationToken)
    {
        var workItem = await _persistence.GetByIdAsync(workItemId, cancellationToken);
        if (workItem is null)
        {
            return (
                null,
                null,
                WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.WorkItemNotFound,
                    $"No work item exists with id '{workItemId}'."
                )
            );
        }

        var template = ResolveTemplate(workItem);
        if (template is null)
        {
            return (
                workItem,
                null,
                WorkItemActionResult.Failure(
                    WorkItemActionFailureCode.UnknownAction,
                    $"Work item {workItemId} references unregistered type '{workItem.TypeId}' and has no stored template snapshot."
                )
            );
        }

        return (workItem, template, null);
    }

    /// <summary>
    /// Pick the template the engine should reason about for a work item.
    /// Delegates to <see cref="WorkItemEngineRules.ResolveTemplate"/> so
    /// bespoke module services resolve the same template the engine does.
    /// </summary>
    private IWorkItemTemplate? ResolveTemplate(WorkItem workItem) =>
        WorkItemEngineRules.ResolveTemplate(workItem, _registry);

    /// <summary>
    /// RA-358: refuse an envelope mutation on a closed case. Assignment and
    /// unassignment are the two operations that reach a work item without
    /// going through <see cref="ApplyActionAsync"/>, so before this check a
    /// direct POST could still hand a withdrawn / approved / rejected item to
    /// someone (the front end only hid the buttons). Terminality comes from
    /// the item's own template via <see cref="TerminalStates.Find"/> — no
    /// state id is hardcoded here, and an in-flight item is judged by the
    /// template version it was submitted under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This deliberately reverses RA-295 AC03, which required assignment to
    /// stay available "all the way through" and is the reason no gate existed.
    /// That was about hand-over: keeping a closed case reassignable so it can
    /// be passed to someone else after the fact. RA-358 trades that capability
    /// away — confirmed with the product owner on 2026-08-04, for every
    /// terminal state rather than withdrawal alone. If hand-over on closed
    /// cases is ever wanted back, it needs a route that is explicitly about
    /// hand-over, not an unguarded assign endpoint.
    /// </para>
    /// <para>
    /// Fails <em>closed</em> when the template cannot be resolved at all,
    /// matching <see cref="ApplyActionAsync"/>, which refuses the same
    /// condition before it ever reaches its terminal check. Otherwise a legacy
    /// pre-snapshot item sitting in a terminal state would become freely
    /// assignable the moment its type was de-registered or renamed — the very
    /// hole this guard exists to close, reached through a different door.
    /// </para>
    /// </remarks>
    /// <param name="operation">
    /// Past-tense verb naming the refused mutation, e.g. <c>"assigned"</c>.
    /// </param>
    private WorkItemActionResult? RequireNonTerminalState(WorkItem workItem, string operation)
    {
        var template = ResolveTemplate(workItem);
        if (template is null)
        {
            _logger.LogWarning(
                "Work item {WorkItemId} refused {Operation}: type {TypeId} is not registered "
                    + "and the item has no template snapshot, so terminality cannot be determined",
                workItem.Id,
                operation,
                workItem.TypeId
            );

            return WorkItemActionResult.Failure(
                WorkItemActionFailureCode.UnknownAction,
                "This case cannot be changed because its type is no longer available."
            );
        }

        // A state the template does not declare carries no terminal metadata,
        // so there is nothing to fail closed on: it is not evidence the case is
        // closed, only that the state is unmodelled. Kept permissive so a
        // mis-seeded state id does not silently freeze assignment; the
        // unresolvable-template case above is the one that hides real terminal
        // states behind an absence.
        if (TerminalStates.Find(template, workItem.StateId) is not { } terminal)
        {
            return null;
        }

        _logger.LogInformation(
            "Work item {WorkItemId} ({TypeId}) refused {Operation}: terminal state {StateId}",
            workItem.Id,
            workItem.TypeId,
            operation,
            terminal.Id
        );

        // RA-358: the BFF renders ProblemDetails.detail verbatim in a
        // user-facing error banner, so the message must name the case in
        // human terms only. Deliberately no work item id here — removing the
        // system-generated GUID from user-visible errors is the point of this
        // story. The id stays in the log line above for support.
        return WorkItemActionResult.Failure(
            WorkItemActionFailureCode.TerminalState,
            $"This case has been {terminal.DisplayName.ToLowerInvariant()} "
                + $"and can no longer be {operation}."
        );
    }

    private string ResolveTemplateVersion(WorkItem workItem) =>
        workItem.TemplateVersion
        ?? workItem.TemplateSnapshot?.TemplateVersion
        ?? _registry.Find(workItem.TypeId)?.TemplateVersion
        ?? "unknown";

    /// <summary>
    /// Append a single entry to the work item's audit log (RA-97). Called
    /// from every engine method on the success path so modules inherit a
    /// complete, automatic audit trail without writing any audit code
    /// themselves. Identity is snapshotted from the supplied principal at
    /// write time.
    /// </summary>
    private static void AppendAudit(
        WorkItem workItem,
        string action,
        string actionDisplayName,
        ClaimsPrincipal? user,
        DateTime createdAt,
        Dictionary<string, string?> details
    )
    {
        workItem.AuditLog.Add(
            new WorkItemAuditEntry
            {
                Action = action,
                ActionDisplayName = actionDisplayName,
                Details = details,
                CreatedAt = createdAt,
                CreatedBy = ResolveActorUserId(user)!,
                CreatedByName = user?.FindFirstValue("user:name"),
                // epr-rr9s: snapshot the state as of this event. Every
                // AppendAudit caller invokes this AFTER any state mutation
                // (submit sets the initial state, action-applied assigns
                // transition.ToStateId first), so workItem.StateId is the
                // resulting post-mutation state for this entry.
                StateId = workItem.StateId,
            }
        );
    }

    private static WorkItemActionResult ConcurrencyConflict(Guid workItemId) =>
        WorkItemActionResult.Failure(
            WorkItemActionFailureCode.ConcurrencyConflict,
            $"Work item '{workItemId}' was modified concurrently. Reload the work item and retry."
        );

    private static string DescribeUser(ClaimsPrincipal? user) =>
        user?.FindFirstValue("user:id")
        ?? user?.FindFirstValue("client_id")
        ?? user?.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? "unknown";

    /// <summary>
    /// Mutating operations require an end-user identity (forwarded by the
    /// BFF as the <c>user:id</c> claim). When it is missing the engine
    /// refuses the call rather than recording an audit entry that cannot be
    /// traced back to a real human — client_id and "unknown" are NOT
    /// acceptable substitutes for accountability.
    /// </summary>
    private static WorkItemActionResult? RequireActorIdentity(ClaimsPrincipal? user)
    {
        if (ResolveActorUserId(user) is not null)
        {
            return null;
        }
        return WorkItemActionResult.Failure(
            WorkItemActionFailureCode.MissingActorIdentity,
            "Mutating this work item requires an authenticated end user; "
                + "the request did not include a 'user:id' claim."
        );
    }

    /// <summary>
    /// The acting end-user's identifier as forwarded by the BFF. Falls back
    /// to <c>null</c> when the request only carries a service identity (e.g.
    /// machine-to-machine calls), which is enough for the service to treat
    /// the call as "no human acting" for assignment purposes.
    /// </summary>
    private static string? ResolveActorUserId(ClaimsPrincipal? user)
    {
        var id = user?.FindFirstValue("user:id");
        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    /// <summary>
    /// Fan out to every registered <see cref="IWorkItemPostActionHook"/>
    /// after a successful submission. Hooks are required to swallow
    /// their own failures (see interface contract); this method only
    /// guards against a misbehaving hook by catching and logging so a
    /// single hook cannot unwind the originating mutation.
    /// </summary>
    private async Task InvokeSubmittedHooksAsync(
        WorkItem workItem,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        foreach (var hook in _postActionHooks)
        {
            try
            {
                await hook.OnSubmittedAsync(workItem, user, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Post-action submit hook {HookType} failed for work item {WorkItemId}",
                    hook.GetType().FullName,
                    workItem.Id
                );
            }
        }
    }

    private async Task InvokeActionAppliedHooksAsync(
        WorkItem workItem,
        string actionId,
        string fromStateId,
        ClaimsPrincipal user
    )
    {
    // The transition is already committed by the time hooks run, so a client
    // that gives up waiting (management-fe times out) must not cancel them:
    // a hook's outbound side effect and its audit entries would be lost,
    // leaving this service and the other system disagreeing about the state.
        foreach (var hook in _postActionHooks)
        {
            try
            {
                await hook.OnActionAppliedAsync(
                    workItem,
                    actionId,
                    fromStateId,
                    user,
                    CancellationToken.None
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Post-action transition hook {HookType} failed for work item {WorkItemId} action {ActionId}",
                    hook.GetType().FullName,
                    workItem.Id,
                    actionId
                );
            }
        }
    }

    /// <summary>
    /// RA-237: fan out to every registered <see cref="IWorkItemPostActionHook"/>
    /// after a successful, non-idempotent assignment change. Mirrors the
    /// swallow-and-log contract of the submit / action fan-outs so a
    /// misbehaving hook (e.g. a Notify outage in the assignment-email hook)
    /// cannot unwind the assignment that has already persisted.
    /// </summary>
    private async Task InvokeAssignmentChangedHooksAsync(
        WorkItem workItem,
        WorkItemAssignmentChange change,
        ClaimsPrincipal user,
        CancellationToken cancellationToken
    )
    {
        foreach (var hook in _postActionHooks)
        {
            try
            {
                await hook.OnAssignmentChangedAsync(workItem, change, user, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Post-action assignment hook {HookType} failed for work item {WorkItemId} change {Change}",
                    hook.GetType().FullName,
                    workItem.Id,
                    change
                );
            }
        }
    }
}
