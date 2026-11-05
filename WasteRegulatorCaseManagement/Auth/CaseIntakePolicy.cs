namespace WasteRegulatorCaseManagement.Auth;

/// <summary>
/// Authorisation policy for creating cases: only the Case Intake Processor,
/// identified by its HMAC-verified client id, may call <c>POST /cases</c>.
/// </summary>
public static class CaseIntakePolicy
{
    public const string Name = "CaseIntake";

    /// <summary>Configuration key overriding <see cref="DefaultClientId"/>.</summary>
    public const string ClientIdKey = "Auth:CaseIntakeClientId";

    public const string DefaultClientId = "case-intake-processor";
}
