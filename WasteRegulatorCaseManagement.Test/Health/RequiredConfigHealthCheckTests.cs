using WasteRegulatorCaseManagement.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace WasteRegulatorCaseManagement.Test.Health;

public class RequiredConfigHealthCheckTests
{
    [Fact]
    public async Task Reports_healthy_when_all_required_config_present()
    {
        var check = MakeCheck();

        var result = await CheckHealth(check);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.True(string.IsNullOrEmpty(result.Description));
    }

    [Fact]
    public async Task Reports_healthy_on_a_vanilla_local_run_with_nothing_set()
    {
        // appsettings.Development.json never sets any of these — a plain
        // `dotnet run` locally must stay healthy.
        var check = MakeCheck(new Dictionary<string, string?>(), isDevelopment: true);

        var result = await CheckHealth(check);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Theory]
    [InlineData("AUTH_SHARED_SECRET:MANAGEMENT_FE", "AUTH_SHARED_SECRET__MANAGEMENT_FE")]
    [InlineData("AUTH_SHARED_SECRET:BACKEND", "AUTH_SHARED_SECRET__BACKEND")]
    public async Task Reports_unhealthy_when_a_caller_secret_is_missing_outside_development(
        string configKey,
        string expectedReportedKey
    )
    {
        var config = CompleteConfig();
        config[configKey] = "";
        var check = MakeCheck(config, isDevelopment: false);

        var result = await CheckHealth(check);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains(expectedReportedKey, result.Description);
    }

    [Theory]
    [InlineData("AUTH_SHARED_SECRET:MANAGEMENT_FE")]
    [InlineData("AUTH_SHARED_SECRET:BACKEND")]
    public async Task Reports_healthy_when_a_caller_secret_is_missing_in_development(string configKey)
    {
        var config = CompleteConfig();
        config[configKey] = "";
        var check = MakeCheck(config, isDevelopment: true);

        var result = await CheckHealth(check);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Lists_every_missing_key_outside_development()
    {
        var config = new Dictionary<string, string?>
        {
            ["AUTH_SHARED_SECRET:MANAGEMENT_FE"] = "",
            ["AUTH_SHARED_SECRET:BACKEND"] = ""
        };
        var check = MakeCheck(config, isDevelopment: false);

        var result = await CheckHealth(check);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("AUTH_SHARED_SECRET__MANAGEMENT_FE", result.Description);
        Assert.Contains("AUTH_SHARED_SECRET__BACKEND", result.Description);
    }

    private static Dictionary<string, string?> CompleteConfig() =>
        new()
        {
            ["AUTH_SHARED_SECRET:MANAGEMENT_FE"] = "management-fe-secret",
            ["AUTH_SHARED_SECRET:BACKEND"] = "backend-secret"
        };

    private static Task<HealthCheckResult> CheckHealth(RequiredConfigHealthCheck check) =>
        check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

    private static RequiredConfigHealthCheck MakeCheck(
        Dictionary<string, string?>? configValues = null,
        bool isDevelopment = false
    )
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues ?? CompleteConfig())
            .Build();

        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName = isDevelopment
            ? Environments.Development
            : Environments.Production;

        return new RequiredConfigHealthCheck(configuration, environment);
    }
}
