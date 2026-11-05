using Microsoft.Extensions.Hosting;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Emits a single startup warning when <c>Cases:SeedOnStartup=true</c>
/// is observed outside a development environment. The actual
/// <see cref="CaseSeederHostedService"/> is intentionally NOT
/// registered in that case (see
/// <see cref="CaseModuleExtensions.AddCaseSeeders"/>),
/// because the seeder writes rows that reference stub user identifiers
/// which must never appear in a real Mongo collection.
/// </summary>
internal sealed class CaseSeederMisconfigurationWarningHostedService(
    IHostEnvironment environment,
    ILogger<CaseSeederMisconfigurationWarningHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "Cases:SeedOnStartup=true was set in '{Environment}' but the seeder " +
            "only runs when the host environment is 'Development'. The seeder will NOT " +
            "execute. Remove the configuration value to silence this warning.",
            environment.EnvironmentName);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
