namespace WasteRegulatorCaseManagement.Notifications;

/// <summary>
/// RA-422: single source of truth for the central "are case management
/// notifications enabled?" decision. <c>ConfigureNotifications</c> in
/// <c>Program.cs</c> registers the no-op Notify client when disabled, so no
/// real email can leave even if something resolves
/// <see cref="INotifyClient"/> directly.
/// Consulted at service-registration time (before the options graph is
/// built), so it reads raw configuration rather than a bound
/// <see cref="NotifyConfig"/>.
/// </summary>
internal static class NotifyFeature
{
    /// <summary>Configuration key backing <see cref="NotifyConfig.Enabled"/>.</summary>
    internal const string EnabledConfigKey = "Notify:Enabled";

    /// <summary>
    /// <c>true</c> only when <c>Notify:Enabled</c> is explicitly configured
    /// true; defaults to <c>false</c> (emails off) when the key is absent.
    /// </summary>
    internal static bool NotificationsEnabled(IConfiguration configuration) =>
        configuration.GetValue(EnabledConfigKey, false);
}
