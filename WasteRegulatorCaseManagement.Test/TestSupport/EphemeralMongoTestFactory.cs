using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace WasteRegulatorCaseManagement.Test.TestSupport;

/// <summary>
/// <see cref="WebApplicationFactory{Program}"/> for tests that only inspect
/// DI registrations / endpoint metadata and never exercise Mongo behaviour
/// (header propagation allow-lists, request-size-limit metadata, notify
/// client registration, seeder gating). Wires the app onto the class
/// fixture's ephemeral mongod (<see cref="MongoIntegrationFixture"/> +
/// <see cref="TestServiceCollectionExtensions.UseEphemeralMongoPersistence"/>)
/// instead of the default, unreachable-in-tests connection string — that
/// otherwise left <c>CasePersistence</c>'s startup index reconciliation
/// to eat a ~90s Mongo server-selection timeout per test.
/// </summary>
public sealed class EphemeralMongoTestFactory : WebApplicationFactory<Program>
{
	private readonly MongoIntegrationFixture _fixture;
	private readonly string _databaseName;
	private readonly string? _environment;
	private readonly IReadOnlyDictionary<string, string?> _settings;

	public EphemeralMongoTestFactory(
		MongoIntegrationFixture fixture,
		string databaseNamePrefix,
		string? environment = null,
		IReadOnlyDictionary<string, string?>? settings = null)
	{
		_fixture = fixture;
		_databaseName = MongoIntegrationFixture.NewDatabaseName(databaseNamePrefix);
		_environment = environment;
		_settings = settings ?? new Dictionary<string, string?>();
	}

	protected override void ConfigureWebHost(IWebHostBuilder builder)
	{
		if (_environment is not null)
		{
			builder.UseEnvironment(_environment);
		}

		foreach (var (key, value) in _settings)
		{
			builder.UseSetting(key, value);
		}

		builder.ConfigureServices(services =>
			services.UseEphemeralMongoPersistence(_fixture, _databaseName));
	}
}
