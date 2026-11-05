using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace WasteRegulatorCaseManagement.Health;

/// <summary>
/// Readiness check for required config that otherwise has no signal at all when
/// missing. <c>AddCallerSecret</c> (Program.cs) treats a blank per-caller shared
/// secret as "caller not configured yet" and silently no-ops — intentional for
/// local development (see appsettings.Development.json, which never sets any of
/// these), but dangerous left unset in a deployed environment where nothing else
/// reports it. Mirrors <c>RequiredConfigHealthCheck</c> in the sibling
/// epr-register-enrol-backend repo (RA-441).
/// </summary>
public sealed class RequiredConfigHealthCheck(IConfiguration configuration, IHostEnvironment environment)
    : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var missing = new List<string>();

        // docs/cdp-deployment.md: both caller secrets are "Required in all
        // non-Development environments." AddCallerSecret has no other signal when
        // one is blank — the caller just never gets an entry in ClientSecrets.
        if (!environment.IsDevelopment())
        {
            if (
                string.IsNullOrWhiteSpace(
                    configuration.GetValue<string>("AUTH_SHARED_SECRET:MANAGEMENT_FE")
                )
            )
                missing.Add("AUTH_SHARED_SECRET__MANAGEMENT_FE");
            if (
                string.IsNullOrWhiteSpace(
                    configuration.GetValue<string>("AUTH_SHARED_SECRET:BACKEND")
                )
            )
                missing.Add("AUTH_SHARED_SECRET__BACKEND");
        }

        return Task.FromResult(
            missing.Count == 0
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"Missing required config: {string.Join(", ", missing)}")
        );
    }
}
