using Microsoft.Extensions.Hosting;

namespace WasteRegulatorCaseManagement.Cases;

/// <summary>
/// Discovers every <see cref="ICaseMigration"/> registered with DI and
/// runs them in sequence at application startup. Mirrors the
/// <see cref="CaseSeederHostedService"/> fault-tolerance posture: a
/// failed migration is logged as a warning and startup continues — the
/// migration will be re-attempted on the next boot.
///
/// Unlike the seeder, migrations run in all environments (including Production)
/// because they are needed to keep existing documents consistent with evolving
/// schema expectations.
/// </summary>
internal sealed class CaseMigrationHostedService(
    IServiceProvider serviceProvider,
    ILogger<CaseMigrationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var migrations = serviceProvider.GetServices<ICaseMigration>().ToList();
        if (migrations.Count == 0)
        {
            return;
        }

        ICasePersistence? persistence;
        try
        {
            persistence = serviceProvider.GetService<ICasePersistence>();
            if (persistence is null)
            {
                logger.LogDebug(
                    "Case persistence not available; skipping {Count} migration(s).",
                    migrations.Count);
                return;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to resolve persistence; skipping {Count} case migration(s). Will retry on next boot.",
                migrations.Count);
            return;
        }

        foreach (var migration in migrations)
        {
            try
            {
                logger.LogInformation(
                    "Applying case migration: {MigrationName}", migration.Name);
                await migration.ApplyAsync(persistence, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Case migration '{MigrationName}' failed; continuing startup. Will retry on next boot.",
                    migration.Name);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
