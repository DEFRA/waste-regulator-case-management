using System.Security.Claims;
using MongoDB.Bson;
using MongoDB.Driver;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Framework service object that drives state transitions for every work
/// item type. Lives in core because the rules ("you cannot act on a closed
/// case", "you cannot invoke an action
/// that does not apply to the current state", etc.) are universal across
/// case types.
/// </summary>
public interface ICaseService
{
	/// <summary>
	/// Create a brand-new case of <paramref name="type"/> and persist
	/// it. Captures a frozen template snapshot, stamps a server-side
	/// submission timestamp from the injected <see cref="TimeProvider"/>,
	/// and appends a single <c>case-created</c> entry to the new
	/// case's timeline so the timeline starts at the
	/// document's birth event rather than the first state change. The
	/// audit entry and the document body are written in the same
	/// <see cref="ICasePersistence.CreateAsync"/> call, attributed to the
	/// calling service as <c>service:&lt;client id&gt;</c>. A <see cref="CaseIntake.SourceSystemId"/> that
	/// already has a case returns that case as an idempotent replay rather
	/// than creating a second one, so a source system can safely resend.
	/// </summary>
	Task<CaseActionResult> SubmitAsync(
		CaseModuleConfig type,
		CaseIntake intake,
		string? submittedBy,
		ClaimsPrincipal user,
		IReadOnlyDictionary<string, string?>? submissionMetadata = null,
		CancellationToken cancellationToken = default
	);

	Task<CaseActionResult> ApplyActionAsync(
		string caseId,
		string actionId,
		IReadOnlyDictionary<string, string>? decision,
		ClaimsPrincipal user,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// Assign (or re-assign) a case to <paramref name="assigneeId"/>,
	/// snapshotting <paramref name="assigneeName"/> alongside the id so list
	/// views do not need a separate user lookup. RA-323: every caseworker
	/// may assign to anyone, including re-assigning an already-assigned
	/// item — there is no separate permission tier.
	/// </summary>
	Task<CaseActionResult> AssignAsync(
		string caseId,
		string assigneeId,
		string? assigneeName,
		ClaimsPrincipal user,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// Clear the current assignment. Any authenticated caseworker may
	/// unassign (RA-323).
	/// </summary>
	Task<CaseActionResult> UnassignAsync(
		string caseId,
		ClaimsPrincipal user,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// Append a free-text case-level note (RA-96). Authoring identity is
	/// snapshotted from the supplied <see cref="ClaimsPrincipal"/> at the
	/// time of the call so the audit narrative survives later directory
	/// changes. Notes are append-only; this is the only mutation the
	/// framework offers for them. Audited as <c>note-added</c>. The
	/// withdraw and decision-rationale flows are the only current callers —
	/// the standalone FE "add a note" feature and task-scoped notes have
	/// been removed.
	/// </summary>
	Task<CaseActionResult> AddNoteAsync(
		string caseId,
		string text,
		ClaimsPrincipal user,
		CancellationToken cancellationToken = default
	);

	/// <summary>
	/// Compute the currently-available actions for a case. Returns
	/// <c>null</c> when no case exists with the supplied id.
	/// </summary>
	Task<CaseEngineProjection?> ProjectAsync(
		string caseId,
		CancellationToken cancellationToken = default
	);

	/// <summary>Project an already-loaded case. Pure; safe to call without I/O.</summary>
	CaseEngineProjection Project(Case caseRecord);
}

/// <summary>Snapshot of a case alongside its engine-derived view.</summary>
public sealed record CaseEngineProjection(
	Case Case,
	string TemplateVersion,
	IReadOnlyCollection<CaseTransition> AvailableActions
);

public sealed class CaseService : ICaseService
{
	private const string SourceKey = "source";

	private readonly CaseRegistry _registry;
	private readonly ICasePersistence _persistence;
	private readonly ICaseCallbackClient _callbackClient;
	private readonly ILogger<CaseService> _logger;
	private readonly IReadOnlyCollection<ICasePostActionHook> _postActionHooks;
	private readonly TimeProvider _timeProvider;

	public CaseService(
		CaseRegistry registry,
		ICasePersistence persistence,
		ICaseCallbackClient callbackClient,
		ILogger<CaseService> logger,
		TimeProvider? timeProvider = null,
		IEnumerable<ICasePostActionHook>? postActionHooks = null
	)
	{
		this._registry = registry;
		this._persistence = persistence;
		_callbackClient = callbackClient;
		this._logger = logger;
		_postActionHooks = postActionHooks?.ToArray() ?? Array.Empty<ICasePostActionHook>();
		_timeProvider = timeProvider ?? TimeProvider.System;
	}

	public async Task<CaseActionResult> SubmitAsync(
		CaseModuleConfig type,
		CaseIntake intake,
		string? submittedBy,
		ClaimsPrincipal user,
		IReadOnlyDictionary<string, string?>? submissionMetadata = null,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentNullException.ThrowIfNull(type);
		ArgumentNullException.ThrowIfNull(intake);

		var existing = await _persistence.FindBySourceSystemIdAsync(
			intake.SourceSystemId,
			cancellationToken
		);
		if (existing is not null)
		{
			return CaseActionResult.IdempotentReplay(existing);
		}

		var now = _timeProvider.GetUtcNow().UtcDateTime;

		var snapshot = CaseTemplateSnapshot.Capture(type);
		var initialState = intake.InitialStateId is { } initialStateId
			? type.States.Single(state => state.Id == initialStateId)
			: type.InitialState;

		var caseRecord = new Case
		{
			CaseType = type.CaseType,
			StateId = initialState.Id,
			IsTerminal = initialState.IsTerminal,
			SubmittedAt = intake.CreatedAt ?? now,
			LastModifiedAt = now,
			SubmittedBy = submittedBy,
			TemplateSnapshot = snapshot,
			TemplateVersion = snapshot.TemplateVersion,
			SourceSystemId = intake.SourceSystemId,
			CallbackUrl = intake.CallbackUrl,
			Nation = intake.Nation,
			OrgId = intake.OrgId,
			ProducerType = intake.ProducerType,
			DisplayData = new Dictionary<string, CaseDisplayField>(intake.DisplayData),
		};

		// Birth event: the timeline must start at submission rather
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
		AppendTimelineEntry(
			caseRecord,
			"case-created",
			"Case submitted",
			ServiceActor(user),
			now,
			new()
			{
				["caseType"] = type.CaseType,
				["stateId"] = caseRecord.StateId,
				["templateVersion"] = snapshot.TemplateVersion,
				[SourceKey] =
					submissionMetadata is not null
					&& submissionMetadata.TryGetValue(SourceKey, out var src)
						? src
						: null,
				["clientId"] = user.FindFirstValue("client_id"),
			}
		);

		try
		{
			await _persistence.CreateAsync(caseRecord, cancellationToken);
		}
		// A concurrent submit of the same source system id won the insert
		// between the lookup above and this write: hand back its case.
		catch (MongoWriteException ex)
			when (ex.WriteError?.Category == ServerErrorCategory.DuplicateKey)
		{
			var winner =
				await _persistence.FindBySourceSystemIdAsync(
					intake.SourceSystemId,
					cancellationToken
				) ?? throw new InvalidOperationException(
					$"Duplicate key creating case {caseRecord.Id}, but no case has source system id '{intake.SourceSystemId}'.",
					ex
				);
			_logger.LogInformation(
				"Case for source system id {SourceSystemId} was created concurrently; returning {CaseId}",
				intake.SourceSystemId,
				winner.Id
			);
			return CaseActionResult.IdempotentReplay(winner);
		}
		catch (Exception ex)
		{
			_logger.LogError(
				ex,
				"MongoDB write failed for case {CaseId} caseType={CaseType} submittedBy={SubmittedBy}: {ExceptionType}",
				caseRecord.Id,
				type.CaseType,
				submittedBy,
				ex.GetType().Name
			);
			throw;
		}

		_logger.LogInformation(
			"Case {CaseId} ({CaseType}) submitted in state {StateId} by {User}",
			caseRecord.Id,
			caseRecord.CaseType,
			caseRecord.StateId,
			DescribeUser(user)
		);

		await InvokeSubmittedHooksAsync(caseRecord, user, cancellationToken);

		return CaseActionResult.Success(caseRecord);
	}

	public async Task<CaseActionResult> ApplyActionAsync(
		string caseId,
		string actionId,
		IReadOnlyDictionary<string, string>? decision,
		ClaimsPrincipal user,
		CancellationToken cancellationToken = default
	)
	{
		if (RequireActorIdentity(user) is { } identityFailure)
		{
			return identityFailure;
		}
		if (RequireCallerNation(user) is { } nationFailure)
		{
			return nationFailure;
		}

		var (caseRecord, template, failure) = await LoadAsync(caseId, cancellationToken);
		if (caseRecord is not null && !CaseNationScope.Includes(user, caseRecord))
		{
			return CaseActionResult.Failure(
				CaseActionFailureCode.CaseNotFound,
				$"No case exists with id '{caseId}'."
			);
		}
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
			return CaseActionResult.Failure(
				CaseActionFailureCode.UnknownAction,
				$"Action '{actionId}' is not declared by case type '{caseRecord!.CaseType}'."
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
				string.Equals(t.FromStateId, caseRecord!.StateId, StringComparison.OrdinalIgnoreCase)
			) ?? actionTransitions[0];

		if (TerminalStates.Find(template, caseRecord!.StateId) is { } currentTerminalState)
		{
			return CaseActionResult.Failure(
				CaseActionFailureCode.TerminalState,
				$"Case {caseId} is in terminal state '{currentTerminalState.Id}'; no actions are allowed."
			);
		}

		if (
			!string.Equals(
				transition.FromStateId,
				caseRecord!.StateId,
				StringComparison.OrdinalIgnoreCase
			)
		)
		{
			return CaseActionResult.Failure(
				CaseActionFailureCode.InvalidTransition,
				$"Action '{actionId}' moves cases from '{transition.FromStateId}', "
					+ $"but {caseId} is in '{caseRecord.StateId}'."
			);
		}

		var missingField = transition.RequiredDecisionFields?.FirstOrDefault(field =>
			decision is null
			|| !decision.TryGetValue(field, out var value)
			|| string.IsNullOrWhiteSpace(value)
		);
		if (missingField is not null)
		{
			return CaseActionResult.Failure(
				CaseActionFailureCode.MissingDecisionField,
				$"Action '{transition.ActionId}' requires decision field '{missingField}'."
			);
		}

		// The source system is told first and the case only moves once it
		// has accepted the decision, so a caseworker sees a failed callback
		// straight away and the case never claims an outcome its source
		// system does not hold.
		if (
			_registry
				.Find(caseRecord.CaseType)
				?.CallbackMappings.FirstOrDefault(mapping =>
					string.Equals(mapping.Key, transition.ToStateId, StringComparison.OrdinalIgnoreCase)
				)
				.Value
			is { } callbackMapping
		)
		{
			var callbackFailure = await SendCallbackAsync(
				caseRecord,
				callbackMapping,
				decision,
				user,
				cancellationToken
			);
			if (callbackFailure is not null)
			{
				return await RecordCallbackFailureAsync(
					caseRecord,
					transition,
					callbackFailure,
					user,
					cancellationToken
				);
			}
		}

		var previousState = caseRecord.StateId;
		var now = _timeProvider.GetUtcNow().UtcDateTime;
		caseRecord.StateId = transition.ToStateId;
		caseRecord.IsTerminal = TerminalStates.Find(template, transition.ToStateId) is not null;
		caseRecord.LastModifiedAt = now;
		var details = new Dictionary<string, string?>
		{
			["actionId"] = transition.ActionId,
			["actionDisplayName"] = transition.DisplayName,
			["fromStateId"] = previousState,
			["toStateId"] = caseRecord.StateId,
		};
		if (decision is { Count: > 0 })
		{
			caseRecord.Decision = new Dictionary<string, string>(decision);
			foreach (var (field, value) in decision)
			{
				details[$"decision.{field}"] = value;
			}
		}
		AppendTimelineEntry(caseRecord, "state-changed", "Action applied", user, now, details);
		try
		{
			await _persistence.ReplaceAsync(caseRecord, cancellationToken);
		}
		catch (CaseConcurrencyException)
		{
			return ConcurrencyConflict(caseRecord.Id);
		}
		_logger.LogInformation(
			"Case {CaseId} ({CaseType}) transitioned from {FromState} to {ToState} via action {ActionId} by {User}",
			caseRecord.Id,
			caseRecord.CaseType,
			previousState,
			caseRecord.StateId,
			transition.ActionId,
			DescribeUser(user)
		);

		await InvokeActionAppliedHooksAsync(
			caseRecord,
			transition.ActionId,
			previousState,
			user
		);

		return CaseActionResult.Success(caseRecord);
	}

	private async Task<string?> SendCallbackAsync(
		Case caseRecord,
		BsonDocument callbackMapping,
		IReadOnlyDictionary<string, string>? decision,
		ClaimsPrincipal user,
		CancellationToken cancellationToken
	)
	{
		var values = new Dictionary<string, string?>
		{
			["actor.id"] = user.FindFirstValue("user:id"),
			["actor.email"] = user.FindFirstValue("user:email"),
			["actor.name"] = user.FindFirstValue("user:name"),
		};
		foreach (var (field, value) in decision ?? new Dictionary<string, string>())
		{
			values[$"decision.{field}"] = value;
		}

		BsonDocument payload;
		try
		{
			payload = CaseCallbackPayload.Render(callbackMapping, values);
		}
		catch (CaseCallbackPayloadException exception)
		{
			return exception.Message;
		}

		return await _callbackClient.SendAsync(
			caseRecord.CaseType,
			caseRecord.CallbackUrl,
			payload,
			cancellationToken
		);
	}

	private async Task<CaseActionResult> RecordCallbackFailureAsync(
		Case caseRecord,
		CaseTransition transition,
		string failure,
		ClaimsPrincipal user,
		CancellationToken cancellationToken
	)
	{
		var now = _timeProvider.GetUtcNow().UtcDateTime;
		caseRecord.LastModifiedAt = now;
		AppendTimelineEntry(
			caseRecord,
			"callback-failed",
			"Callback failed",
			user,
			now,
			new()
			{
				["actionId"] = transition.ActionId,
				["toStateId"] = transition.ToStateId,
				["reason"] = failure,
			}
		);
		try
		{
			await _persistence.ReplaceAsync(caseRecord, cancellationToken);
		}
		catch (CaseConcurrencyException)
		{
			return ConcurrencyConflict(caseRecord.Id);
		}
		_logger.LogWarning(
			"Case {CaseId} ({CaseType}) stayed in {StateId}: callback for action {ActionId} failed: {Failure}",
			caseRecord.Id,
			caseRecord.CaseType,
			caseRecord.StateId,
			transition.ActionId,
			failure
		);

		return CaseActionResult.Failure(
			CaseActionFailureCode.CallbackFailed,
			$"The source system did not accept '{transition.ActionId}': {failure}"
		);
	}

	public async Task<CaseActionResult> AssignAsync(
		string caseId,
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
		if (RequireCallerNation(user) is { } nationFailure)
		{
			return nationFailure;
		}

		if (string.IsNullOrWhiteSpace(assigneeId))
		{
			return CaseActionResult.Failure(
				CaseActionFailureCode.InvalidAssignment,
				"Assignee id is required."
			);
		}

		var caseRecord = await _persistence.GetByIdAsync(caseId, cancellationToken);
		if (caseRecord is null || !CaseNationScope.Includes(user, caseRecord))
		{
			return CaseActionResult.Failure(
				CaseActionFailureCode.CaseNotFound,
				$"No case exists with id '{caseId}'."
			);
		}

		if (RequireNonTerminalState(caseRecord, "assigned") is { } terminalFailure)
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
			string.Equals(caseRecord.AssignedToId, trimmedAssigneeId, StringComparison.Ordinal)
			&& string.Equals(caseRecord.AssignedToName, snapshotName, StringComparison.Ordinal);

		if (alreadyAssignedToSameUser)
		{
			// Re-assigning to the same user is a no-op: persist nothing,
			// write no audit entry, but tell the caller this was a replay
			// (parity with CompleteTaskAsync) so the endpoint can surface
			// X-Idempotent-Replay: true and the UI can render an
			// appropriate state instead of a misleading "newly assigned".
			return CaseActionResult.IdempotentReplay(caseRecord);
		}

		var previousAssigneeId = caseRecord.AssignedToId;
		var previousAssigneeName = caseRecord.AssignedToName;
		caseRecord.AssignedToId = trimmedAssigneeId;
		caseRecord.AssignedToName = snapshotName;
		caseRecord.AssignedAt = _timeProvider.GetUtcNow().UtcDateTime;
		caseRecord.AssignedBy = actorUserId!;
		caseRecord.LastModifiedAt = caseRecord.AssignedAt.Value;
		AppendTimelineEntry(
			caseRecord,
			"assigned",
			"Assigned",
			user,
			caseRecord.AssignedAt.Value,
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
			await _persistence.ReplaceAsync(caseRecord, cancellationToken);
		}
		catch (CaseConcurrencyException)
		{
			return ConcurrencyConflict(caseRecord.Id);
		}
		_logger.LogInformation(
			"Case {CaseId} ({CaseType}) assigned from {PreviousAssignee} to {NewAssignee} by {User}",
			caseRecord.Id,
			caseRecord.CaseType,
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
			? CaseAssignmentChange.Assigned
			: CaseAssignmentChange.Reassigned;
		await InvokeAssignmentChangedHooksAsync(caseRecord, change, user, cancellationToken);

		return CaseActionResult.Success(caseRecord);
	}

	public async Task<CaseActionResult> UnassignAsync(
		string caseId,
		ClaimsPrincipal user,
		CancellationToken cancellationToken = default
	)
	{
		if (RequireActorIdentity(user) is { } identityFailure)
		{
			return identityFailure;
		}
		if (RequireCallerNation(user) is { } nationFailure)
		{
			return nationFailure;
		}

		var caseRecord = await _persistence.GetByIdAsync(caseId, cancellationToken);
		if (caseRecord is null || !CaseNationScope.Includes(user, caseRecord))
		{
			return CaseActionResult.Failure(
				CaseActionFailureCode.CaseNotFound,
				$"No case exists with id '{caseId}'."
			);
		}

		if (RequireNonTerminalState(caseRecord, "unassigned") is { } terminalFailure)
		{
			return terminalFailure;
		}

		if (caseRecord.AssignedToId is null)
		{
			// Idempotent: already unassigned, nothing to do. Flag as a
			// replay (parity with CompleteTaskAsync / AssignAsync) so the
			// endpoint can set X-Idempotent-Replay: true.
			return CaseActionResult.IdempotentReplay(caseRecord);
		}

		var previousAssigneeId = caseRecord.AssignedToId;
		var previousAssigneeName = caseRecord.AssignedToName;
		var now = _timeProvider.GetUtcNow().UtcDateTime;
		caseRecord.AssignedToId = null;
		caseRecord.AssignedToName = null;
		caseRecord.AssignedAt = null;
		caseRecord.AssignedBy = null;
		caseRecord.LastModifiedAt = now;
		AppendTimelineEntry(
			caseRecord,
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
			await _persistence.ReplaceAsync(caseRecord, cancellationToken);
		}
		catch (CaseConcurrencyException)
		{
			return ConcurrencyConflict(caseRecord.Id);
		}
		_logger.LogInformation(
			"Case {CaseId} ({CaseType}) unassigned (was {PreviousAssignee}) by {User}",
			caseRecord.Id,
			caseRecord.CaseType,
			previousAssigneeId,
			DescribeUser(user)
		);

		// RA-237: notify the post-action hooks of the real unassignment.
		// The earlier IdempotentReplay return (already unassigned) skips this.
		await InvokeAssignmentChangedHooksAsync(
			caseRecord,
			CaseAssignmentChange.Unassigned,
			user,
			cancellationToken
		);

		return CaseActionResult.Success(caseRecord);
	}

	/// <summary>
	/// Maximum length of a single note body. Picked to comfortably hold a
	/// long paragraph of assessor narrative without enabling the field as a
	/// dumping ground for documents. Enforced at the service boundary so
	/// callers see the same limit regardless of transport.
	/// </summary>
	public const int MaxNoteLength = 4000;

	public async Task<CaseActionResult> AddNoteAsync(
		string caseId,
		string text,
		ClaimsPrincipal user,
		CancellationToken cancellationToken = default
	)
	{
		if (RequireActorIdentity(user) is { } identityFailure)
		{
			return identityFailure;
		}
		if (RequireCallerNation(user) is { } nationFailure)
		{
			return nationFailure;
		}

		if (ValidateNoteText(text, out var trimmed, out var noteFailure) is false)
		{
			return noteFailure!;
		}

		// Case-level note (RA-96): a single GetByIdAsync, no template
		// resolution required, so the note still works for legacy items
		// whose case type is no longer configured and which carry no
		// template snapshot.
		var caseRecord = await _persistence.GetByIdAsync(caseId, cancellationToken);
		if (caseRecord is null || !CaseNationScope.Includes(user, caseRecord))
		{
			return CaseActionResult.Failure(
				CaseActionFailureCode.CaseNotFound,
				$"No case exists with id '{caseId}'."
			);
		}

		var now = _timeProvider.GetUtcNow().UtcDateTime;
		caseRecord.LastModifiedAt = now;

		AppendTimelineEntry(
			caseRecord,
			"note-added",
			"Note added",
			user,
			now,
			new() { ["text"] = trimmed }
		);

		try
		{
			await _persistence.ReplaceAsync(caseRecord, cancellationToken);
		}
		catch (CaseConcurrencyException)
		{
			return ConcurrencyConflict(caseRecord.Id);
		}
		_logger.LogInformation(
			"Note added to case {CaseId} ({CaseType}) by {User}",
			caseRecord.Id,
			caseRecord.CaseType,
			DescribeUser(user)
		);

		return CaseActionResult.Success(caseRecord);
	}

	private static bool ValidateNoteText(
		string text,
		out string? trimmed,
		out CaseActionResult? failure
	)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			trimmed = null;
			failure = CaseActionResult.Failure(
				CaseActionFailureCode.InvalidNote,
				"Note text is required."
			);
			return false;
		}

		var t = text.Trim();
		if (t.Length > MaxNoteLength)
		{
			trimmed = null;
			failure = CaseActionResult.Failure(
				CaseActionFailureCode.InvalidNote,
				$"Note text must be {MaxNoteLength} characters or fewer."
			);
			return false;
		}

		trimmed = t;
		failure = null;
		return true;
	}

	public async Task<CaseEngineProjection?> ProjectAsync(
		string caseId,
		CancellationToken cancellationToken = default
	)
	{
		var caseRecord = await _persistence.GetByIdAsync(caseId, cancellationToken);
		return caseRecord is null ? null : Project(caseRecord);
	}

	public CaseEngineProjection Project(Case caseRecord)
	{
		ArgumentNullException.ThrowIfNull(caseRecord);

		var template = ResolveTemplate(caseRecord);
		if (template is null)
		{
			// The case exists but its case type is no longer configured and
			// no snapshot is on file (e.g. a legacy item). Render it as having
			// no available actions so callers can still display it.
			return new CaseEngineProjection(
				caseRecord,
				ResolveTemplateVersion(caseRecord),
				Array.Empty<CaseTransition>()
			);
		}

		var isTerminal = TerminalStates.Find(template, caseRecord.StateId) is not null;

		IReadOnlyCollection<CaseTransition> available = isTerminal
			? Array.Empty<CaseTransition>()
			: template
				.Transitions.Where(t =>
					string.Equals(
						t.FromStateId,
						caseRecord.StateId,
						StringComparison.OrdinalIgnoreCase
					)
				)
				.ToList();

		return new CaseEngineProjection(
			caseRecord,
			template.TemplateVersion,
			available
		);
	}

	private async Task<(
		Case? Case,
		ICaseTemplate? Template,
		CaseActionResult? Failure
	)> LoadAsync(string caseId, CancellationToken cancellationToken)
	{
		var caseRecord = await _persistence.GetByIdAsync(caseId, cancellationToken);
		if (caseRecord is null)
		{
			return (
				null,
				null,
				CaseActionResult.Failure(
					CaseActionFailureCode.CaseNotFound,
					$"No case exists with id '{caseId}'."
				)
			);
		}

		var template = ResolveTemplate(caseRecord);
		if (template is null)
		{
			return (
				caseRecord,
				null,
				CaseActionResult.Failure(
					CaseActionFailureCode.UnknownAction,
					$"Case {caseId} references unregistered type '{caseRecord.CaseType}' and has no stored template snapshot."
				)
			);
		}

		return (caseRecord, template, null);
	}

	/// <summary>
	/// Pick the template the engine should reason about for a case. The
	/// snapshot stored on the case wins so that historical items keep
	/// their original action set even if the live type has since changed; the
	/// live type is used only as a fallback for items submitted without a
	/// snapshot.
	/// </summary>
	private ICaseTemplate? ResolveTemplate(Case caseRecord) =>
		caseRecord.TemplateSnapshot ?? (ICaseTemplate?)_registry.Find(caseRecord.CaseType);

	/// <summary>
	/// RA-358: refuse an envelope mutation on a closed case. Assignment and
	/// unassignment are the two operations that reach a case without
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
	private CaseActionResult? RequireNonTerminalState(Case caseRecord, string operation)
	{
		var template = ResolveTemplate(caseRecord);
		if (template is null)
		{
			_logger.LogWarning(
				"Case {CaseId} refused {Operation}: type {CaseType} is not registered "
					+ "and the item has no template snapshot, so terminality cannot be determined",
				caseRecord.Id,
				operation,
				caseRecord.CaseType
			);

			return CaseActionResult.Failure(
				CaseActionFailureCode.UnknownAction,
				"This case cannot be changed because its type is no longer available."
			);
		}

		// A state the template does not declare carries no terminal metadata,
		// so there is nothing to fail closed on: it is not evidence the case is
		// closed, only that the state is unmodelled. Kept permissive so a
		// mis-seeded state id does not silently freeze assignment; the
		// unresolvable-template case above is the one that hides real terminal
		// states behind an absence.
		if (TerminalStates.Find(template, caseRecord.StateId) is not { } terminal)
		{
			return null;
		}

		_logger.LogInformation(
			"Case {CaseId} ({CaseType}) refused {Operation}: terminal state {StateId}",
			caseRecord.Id,
			caseRecord.CaseType,
			operation,
			terminal.Id
		);

		// RA-358: the BFF renders ProblemDetails.detail verbatim in a
		// user-facing error banner, so the message must name the case in
		// human terms only. Deliberately no case id here — removing the
		// system-generated GUID from user-visible errors is the point of this
		// story. The id stays in the log line above for support.
		return CaseActionResult.Failure(
			CaseActionFailureCode.TerminalState,
			$"This case has been {terminal.DisplayName.ToLowerInvariant()} "
				+ $"and can no longer be {operation}."
		);
	}

	private string ResolveTemplateVersion(Case caseRecord) =>
		caseRecord.TemplateVersion
		?? caseRecord.TemplateSnapshot?.TemplateVersion
		?? _registry.Find(caseRecord.CaseType)?.TemplateVersion
		?? "unknown";

	/// <summary>
	/// Append a single entry to the case's timeline (RA-97). Called
	/// from every engine method on the success path so every case type
	/// has a complete, automatic audit trail. Identity is snapshotted from the supplied principal at
	/// write time.
	/// </summary>
	private static void AppendTimelineEntry(
		Case caseRecord,
		string action,
		string actionDisplayName,
		ClaimsPrincipal? user,
		DateTime performedAt,
		Dictionary<string, string?> details
	) =>
		AppendTimelineEntry(
			caseRecord,
			action,
			actionDisplayName,
			new Actor(ResolveActorUserId(user)!, user?.FindFirstValue("user:name")),
			performedAt,
			details
		);

	/// <summary>
	/// Cases are created by a source system rather than a person, so the
	/// birth entry names the calling service by its verified client id.
	/// </summary>
	private static Actor ServiceActor(ClaimsPrincipal user)
	{
		var clientId = user.FindFirstValue("client_id");
		return new Actor($"service:{clientId}", clientId);
	}

	private sealed record Actor(string Id, string? Name);

	private static void AppendTimelineEntry(
		Case caseRecord,
		string action,
		string actionDisplayName,
		Actor actor,
		DateTime performedAt,
		Dictionary<string, string?> details
	)
	{
		caseRecord.Timeline.Add(
			new CaseTimelineEntry
			{
				Action = action,
				ActionDisplayName = actionDisplayName,
				Details = details,
				PerformedAt = performedAt,
				PerformedById = actor.Id,
				PerformedByName = actor.Name,
				// epr-rr9s: snapshot the state as of this event. Every
				// AppendAudit caller invokes this AFTER any state mutation
				// (submit sets the initial state, state-changed assigns
				// transition.ToStateId first), so caseRecord.StateId is the
				// resulting post-mutation state for this entry.
				StateId = caseRecord.StateId,
			}
		);
	}

	private static CaseActionResult ConcurrencyConflict(string caseId) =>
		CaseActionResult.Failure(
			CaseActionFailureCode.ConcurrencyConflict,
			$"Case '{caseId}' was modified concurrently. Reload the case and retry."
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
	private static CaseActionResult? RequireActorIdentity(ClaimsPrincipal? user)
	{
		if (ResolveActorUserId(user) is not null)
		{
			return null;
		}
		return CaseActionResult.Failure(
			CaseActionFailureCode.MissingActorIdentity,
			"Mutating this case requires an authenticated end user; "
				+ "the request did not include a 'user:id' claim."
		);
	}

	/// <summary>
	/// The acting end-user's identifier as forwarded by the BFF. Falls back
	/// to <c>null</c> when the request only carries a service identity (e.g.
	/// machine-to-machine calls), which is enough for the service to treat
	/// the call as "no human acting" for assignment purposes.
	/// </summary>
	private static CaseActionResult? RequireCallerNation(ClaimsPrincipal? user)
	{
		if (CaseNationScope.CallerNation(user) is not null)
		{
			return null;
		}
		return CaseActionResult.Failure(
			CaseActionFailureCode.NotAuthorized,
			"Acting on a case requires a 'user:nation' claim scoping the caller to a nation."
		);
	}

	private static string? ResolveActorUserId(ClaimsPrincipal? user)
	{
		var id = user?.FindFirstValue("user:id");
		return string.IsNullOrWhiteSpace(id) ? null : id;
	}

	/// <summary>
	/// Fan out to every registered <see cref="ICasePostActionHook"/>
	/// after a successful submission. Hooks are required to swallow
	/// their own failures (see interface contract); this method only
	/// guards against a misbehaving hook by catching and logging so a
	/// single hook cannot unwind the originating mutation.
	/// </summary>
	private async Task InvokeSubmittedHooksAsync(
		Case caseRecord,
		ClaimsPrincipal user,
		CancellationToken cancellationToken
	)
	{
		foreach (var hook in _postActionHooks)
		{
			try
			{
				await hook.OnSubmittedAsync(caseRecord, user, cancellationToken);
			}
			catch (Exception ex)
			{
				_logger.LogError(
					ex,
					"Post-action submit hook {HookType} failed for case {CaseId}",
					hook.GetType().FullName,
					caseRecord.Id
				);
			}
		}
	}

	private async Task InvokeActionAppliedHooksAsync(
		Case caseRecord,
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
					caseRecord,
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
					"Post-action transition hook {HookType} failed for case {CaseId} action {ActionId}",
					hook.GetType().FullName,
					caseRecord.Id,
					actionId
				);
			}
		}
	}

	/// <summary>
	/// RA-237: fan out to every registered <see cref="ICasePostActionHook"/>
	/// after a successful, non-idempotent assignment change. Mirrors the
	/// swallow-and-log contract of the submit / action fan-outs so a
	/// misbehaving hook (e.g. a Notify outage in the assignment-email hook)
	/// cannot unwind the assignment that has already persisted.
	/// </summary>
	private async Task InvokeAssignmentChangedHooksAsync(
		Case caseRecord,
		CaseAssignmentChange change,
		ClaimsPrincipal user,
		CancellationToken cancellationToken
	)
	{
		foreach (var hook in _postActionHooks)
		{
			try
			{
				await hook.OnAssignmentChangedAsync(caseRecord, change, user, cancellationToken);
			}
			catch (Exception ex)
			{
				_logger.LogError(
					ex,
					"Post-action assignment hook {HookType} failed for case {CaseId} change {Change}",
					hook.GetType().FullName,
					caseRecord.Id,
					change
				);
			}
		}
	}
}
