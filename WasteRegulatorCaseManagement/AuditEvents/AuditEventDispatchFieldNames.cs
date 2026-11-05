namespace WasteRegulatorCaseManagement.AuditEvents;

internal static class AuditEventDispatchFieldNames
{
    private static readonly string Dispatches = ToCamelCase(nameof(AuditEvent.Dispatches));
    private static readonly string NextAttemptAt = ToCamelCase(nameof(AuditEventDispatch.NextAttemptAt));
    private static readonly string Status = ToCamelCase(nameof(AuditEventDispatch.Status));

    public static string DispatchPath(string processName) => $"{Dispatches}.{processName}";

    public static string DispatchStatusPath(string processName) => $"{DispatchPath(processName)}.{Status}";

    public static string DispatchNextAttemptAtPath(string processName) =>
        $"{DispatchPath(processName)}.{NextAttemptAt}";

    private static string ToCamelCase(string value) => char.ToLowerInvariant(value[0]) + value[1..];
}
