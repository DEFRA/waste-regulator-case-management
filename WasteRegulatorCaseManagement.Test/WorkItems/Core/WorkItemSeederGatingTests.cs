using WasteRegulatorCaseManagement.Test.TestSupport;
using WasteRegulatorCaseManagement.WorkItems.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace WasteRegulatorCaseManagement.Test.WorkItems.Core;

/// <summary>
/// epr-nqe: <see cref="WorkItemSeederHostedService"/> writes records that
/// reference stub user identifiers, so it must only be wired up when the
/// host is in Development AND <c>WorkItems:SeedOnStartup=true</c>. These
/// tests pin the gating contract so a misconfigured production deploy
/// cannot pollute the live Mongo collection.
///
/// The seed flag is supplied via <see cref="IWebHostBuilder.UseSetting"/>,
/// which feeds <c>WebApplicationBuilder.Configuration</c> before
/// <c>Program.ConfigureServices</c> runs. We deliberately do NOT mutate
/// the process-global <c>WorkItems__SeedOnStartup</c> environment
/// variable here: xUnit v3 parallelises test classes by default, and a
/// global mutation would race with any other test class spinning up a
/// <see cref="WebApplicationFactory{T}"/> at the same time and cause its
/// host to register the seeder, which would then fire
/// <c>QueryAsync(Page=1, PageSize=1)</c> against that test's mock
/// persistence. (Surfaced by epr-mf7 — CI was slow enough to overlap
/// these factories where local timing did not.)
/// </summary>
public class WorkItemSeederGatingTests
{
    private const string SeedFlagConfigKey = "WorkItems:SeedOnStartup";
    private readonly MongoIntegrationFixture _fixture;

    public WorkItemSeederGatingTests(MongoIntegrationFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Gate matrix for <see cref="WorkItemModuleExtensions.AddWorkItemSeeders"/>:
    /// the demo set needs Development + the flag; a non-Development flag only
    /// warns.
    /// </summary>
    [Theory]
    [InlineData("Development", true, true, false)]
    [InlineData("Development", false, false, false)]
    [InlineData("Production", true, false, true)]
    [InlineData("Production", false, false, false)]
    public void Seeders_registered_match_the_environment_and_flag(
        string environment,
        bool seedOnStartup,
        bool expectDemoSeeder,
        bool expectMisconfigurationWarning
    )
    {
        using var factory = CreateFactory(environment, seedOnStartup);

        var hostedServices = factory.Services.GetServices<IHostedService>().ToList();

        Assert.Equal(expectDemoSeeder, hostedServices.Any(s => s is WorkItemSeederHostedService));
        Assert.Equal(
            expectMisconfigurationWarning,
            hostedServices.Any(s => s is WorkItemSeederMisconfigurationWarningHostedService)
        );
    }

    private EphemeralMongoTestFactory CreateFactory(string environment, bool seedOnStartup) =>
        new(_fixture, "seeder-gating", environment, new Dictionary<string, string?>
        {
            [SeedFlagConfigKey] = seedOnStartup ? "true" : "false",
        });
}
