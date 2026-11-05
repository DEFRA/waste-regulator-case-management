using WasteRegulatorCaseManagement.Utils.Mongo;
using WasteRegulatorCaseManagement.Cases;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace WasteRegulatorCaseManagement.Test.TestSupport;

/// <summary>
/// Helpers for swapping the production Mongo wiring in a
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>-
/// based test for the ephemeral mongod managed by
/// <see cref="MongoIntegrationFixture"/>. Centralised so endpoint
/// suites do not silently diverge in how they wire the DI container.
/// </summary>
public static class TestServiceCollectionExtensions
{
    /// <summary>
    /// Replace <see cref="IMongoDbClientFactory"/> and
    /// <see cref="ICasePersistence"/> with implementations that
    /// talk to <paramref name="fixture"/>'s ephemeral mongod against
    /// a fresh per-test <paramref name="databaseName"/>. The
    /// production <see cref="CasePersistence"/> class is used
    /// verbatim — only its connection target is swapped.
    /// </summary>
    public static IServiceCollection UseEphemeralMongoPersistence(
        this IServiceCollection services,
        MongoIntegrationFixture fixture,
        string databaseName)
    {
        services.RemoveAll<ICasePersistence>();
        services.RemoveAll<IMongoDbClientFactory>();

        var clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, databaseName);
        services.AddSingleton<IMongoDbClientFactory>(clientFactory);
        services.AddSingleton<ICasePersistence>(sp =>
            new CasePersistence(clientFactory, sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<ICaseRegistry>()));

        return services;
    }
}
