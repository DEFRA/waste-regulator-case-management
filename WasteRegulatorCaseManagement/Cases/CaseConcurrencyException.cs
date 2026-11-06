namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Thrown by <see cref="ICasePersistence.ReplaceAsync"/> when the saved
/// version no longer matches the version that was loaded — i.e. another
/// caller mutated the case first. The engine catches this and surfaces
/// it as <see cref="CaseActionFailureCode.ConcurrencyConflict"/> (HTTP
/// 409) so the caller can retry against the latest state.
/// </summary>
public sealed class CaseConcurrencyException(string caseId, int expectedVersion)
	: Exception($"Case '{caseId}' was modified concurrently (expected version {expectedVersion}).")
{
	public string CaseId { get; } = caseId;
	public int ExpectedVersion { get; } = expectedVersion;
}
