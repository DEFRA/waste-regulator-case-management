namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// The source system's description of a new case, as validated at the
/// intake endpoint. See <see cref="Case"/> for what each field means.
/// </summary>
public sealed record CaseIntake(
    string SourceSystemId,
    string CallbackUrl,
    string Nation,
    string OrgId,
    string? ProducerType,
    IReadOnlyDictionary<string, CaseDisplayField> DisplayData
);
