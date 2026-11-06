namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Failure reasons returned by the case engine. Endpoints translate
/// these into HTTP problem responses.
/// </summary>
public enum CaseActionFailureCode
{
	CaseNotFound,
	UnknownAction,
	InvalidTransition,
	TerminalState,

	/// <summary>
	/// The caller is not allowed to act on cases: it carries no
	/// <c>user:nation</c> claim.
	/// </summary>
	NotAuthorized,

	/// <summary>
	/// The assign request was structurally invalid (e.g. blank assignee id).
	/// </summary>
	InvalidAssignment,

	/// <summary>
	/// A request to add a note was structurally invalid (e.g. blank text or
	/// over the size limit).
	/// </summary>
	InvalidNote,

	/// <summary>
	/// The action's decision omitted a field its transition requires.
	/// </summary>
	MissingDecisionField,

	/// <summary>
	/// The case's source system did not accept the decision, so the case
	/// was left in its current state.
	/// </summary>
	CallbackFailed,

	/// <summary>
	/// The case was modified by another caller between load and save
	/// (optimistic concurrency conflict). Retry the request after re-reading
	/// the latest state.
	/// </summary>
	ConcurrencyConflict,

	/// <summary>
	/// The caller did not present an end-user identity (the BFF must
	/// forward a <c>user:id</c> claim). Mutating operations refuse to write
	/// audit entries that cannot be tied back to a real human, so without
	/// this claim we 401 the request rather than persist a placeholder.
	/// </summary>
	MissingActorIdentity,
}

/// <summary>
/// Result of a state-changing operation. Either succeeds with the
/// updated <see cref="Case"/>, or fails with a <see cref="CaseActionFailureCode"/>
/// and human-readable message.
/// </summary>
public sealed record CaseActionResult
{
	private CaseActionResult(
		Case? caseRecord,
		CaseActionFailureCode? failureCode,
		string? message,
		bool isIdempotentReplay
	)
	{
		Case = caseRecord;
		FailureCode = failureCode;
		Message = message;
		IsIdempotentReplay = isIdempotentReplay;
	}

	public Case? Case { get; }
	public CaseActionFailureCode? FailureCode { get; }
	public string? Message { get; }

	/// <summary>
	/// True when this success is the second-or-later call that performed
	/// the same action — no state changed and no audit entry was written
	/// because the operation had already been applied. Endpoints surface
	/// this via the <c>X-Idempotent-Replay: true</c> response header so
	/// clients can distinguish "first hit" from "replay".
	/// </summary>
	public bool IsIdempotentReplay { get; }

	public bool IsSuccess => FailureCode is null;

	public static CaseActionResult Success(Case caseRecord) =>
		new(caseRecord, failureCode: null, message: null, isIdempotentReplay: false);

	/// <summary>
	/// Same as <see cref="Success"/> but flags the result as a no-op replay
	/// of an already-applied action.
	/// </summary>
	public static CaseActionResult IdempotentReplay(Case caseRecord) =>
		new(caseRecord, failureCode: null, message: null, isIdempotentReplay: true);

	public static CaseActionResult Failure(CaseActionFailureCode code, string message) =>
		new(caseRecord: null, code, message, isIdempotentReplay: false);
}
