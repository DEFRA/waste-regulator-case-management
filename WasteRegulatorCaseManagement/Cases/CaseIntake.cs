namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// The source system's description of a new case, as validated at the
/// intake endpoint. See <see cref="Case"/> for what each field means.
/// <see cref="InitialStateId"/> and <see cref="CreatedAt"/> are only for
/// loading submissions that predate this service: they override the type's
/// initial state and the submission time so already-decided cases arrive
/// decided and keep their original queue order.
/// </summary>
public sealed record CaseIntake(
	string SourceSystemId,
	string CallbackUrl,
	string Nation,
	string OrgId,
	string? ProducerType,
	IReadOnlyDictionary<string, CaseDisplayField> DisplayData,
	string? InitialStateId = null,
	DateTime? CreatedAt = null
);
