using WasteRegulatorCaseManagement.Config;
using WasteRegulatorCaseManagement.Test.Notifications;
using WasteRegulatorCaseManagement.Test.TestSupport;
using WasteRegulatorCaseManagement.WorkItems.Core;
using Microsoft.Extensions.DependencyInjection;

namespace WasteRegulatorCaseManagement.Test.Config;

/// <summary>
/// RA-000: boots the real Program.cs wiring to prove what
/// <c>TestOrganisations__Enabled</c> changes — the prod startup guard. In the env-var collection because the guard
/// reads the process-global CDP <c>ENVIRONMENT</c> variable.
/// </summary>
[Collection(EnvVarMutationCollection.Name)]
public class TestOrganisationsWiringTests : IClassFixture<MongoIntegrationFixture>
{
    private readonly MongoIntegrationFixture _fixture;

    public TestOrganisationsWiringTests(MongoIntegrationFixture fixture) => _fixture = fixture;

    private EphemeralMongoTestFactory NewFactory(bool? testOrganisations)
    {
        var settings = new Dictionary<string, string?>();
        if (testOrganisations is not null)
        {
            settings[TestOrganisations.EnabledKey] = testOrganisations.Value ? "true" : "false";
        }
        return new EphemeralMongoTestFactory(_fixture, "test-orgs", settings: settings);
    }

    [Fact]
    public void Startup_fails_when_test_organisations_are_enabled_in_prod()
    {
        WithCdpEnvironment(
            "prod",
            () =>
            {
                using var factory = NewFactory(testOrganisations: true);

                var ex = Assert.ThrowsAny<Exception>(() => factory.Services);
                Assert.Contains(
                    "TestOrganisations__Enabled",
                    ex.ToString(),
                    StringComparison.Ordinal
                );
            }
        );
    }

    [Fact]
    public void Startup_succeeds_in_prod_when_test_organisations_are_off()
    {
        WithCdpEnvironment(
            "prod",
            () =>
            {
                using var factory = NewFactory(testOrganisations: null);

                Assert.NotNull(factory.Services.GetRequiredService<IWorkItemService>());
            }
        );
    }

    private static void WithCdpEnvironment(string value, Action act)
    {
        var previous = Environment.GetEnvironmentVariable(CdpEnvironment.Variable);
        try
        {
            Environment.SetEnvironmentVariable(CdpEnvironment.Variable, value);
            act();
        }
        finally
        {
            Environment.SetEnvironmentVariable(CdpEnvironment.Variable, previous);
        }
    }
}
