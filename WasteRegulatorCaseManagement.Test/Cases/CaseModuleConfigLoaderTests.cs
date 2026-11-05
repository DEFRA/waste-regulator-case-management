using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using WasteRegulatorCaseManagement.Cases;
using WasteRegulatorCaseManagement.Test.TestSupport;

namespace WasteRegulatorCaseManagement.Test.Cases;

public sealed class CaseModuleConfigLoaderTests : IAsyncDisposable
{
	private readonly string _databaseName = MongoIntegrationFixture.NewDatabaseName("case_module_config");
	private readonly TestMongoDbClientFactory _clientFactory;
	private readonly CaseRegistry _registry = new([]);
	private readonly CaseModuleConfigLoader _loader;

	public CaseModuleConfigLoaderTests(MongoIntegrationFixture fixture)
	{
		_clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
		_loader = new CaseModuleConfigLoader(
			new CaseModuleConfigPersistence(_clientFactory, NullLoggerFactory.Instance),
			_registry,
			NullLogger<CaseModuleConfigLoader>.Instance
		);
	}

	public async ValueTask DisposeAsync() =>
		await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);

	[Fact]
	public async Task Start_writes_the_embedded_CSoC_configuration_to_the_database()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;

		// Act
		await _loader.StartAsync(cancellationToken);

		// Assert
		var stored = await _clientFactory
			.GetCollection<CaseModuleConfig>("caseModuleConfig")
			.Find(c => c.CaseType == "CSoC")
			.SingleAsync(cancellationToken);
		Assert.Equal("SUBMITTED", stored.InitialStateId);
		Assert.Equal(
			["ACCEPTED", "CANCELLED"],
			stored.States.Where(s => s.IsTerminal).Select(s => s.Id).ToArray()
		);
		Assert.Equal(
			["accept", "cancel"],
			stored.Transitions.Select(t => t.ActionId).ToArray()
		);
		Assert.Equal("{{decision.reason}}", stored.CallbackMappings["CANCELLED"]["reason"].AsString);
	}

	[Fact]
	public async Task Start_loads_a_case_type_that_exists_only_in_the_database()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await _clientFactory
			.GetCollection<CaseModuleConfig>("caseModuleConfig")
			.InsertOneAsync(TestCaseType.Create("database-only", "Database only"), cancellationToken: cancellationToken);

		// Act
		await _loader.StartAsync(cancellationToken);

		// Assert
		Assert.NotNull(_registry.Find("database-only"));
		Assert.NotNull(_registry.Find("CSoC"));
	}

	[Fact]
	public async Task Start_replaces_a_stored_copy_of_an_embedded_case_type()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		var stale = TestCaseType.Create("CSoC", "Stale CSoC");
		await _clientFactory
			.GetCollection<CaseModuleConfig>("caseModuleConfig")
			.InsertOneAsync(stale, cancellationToken: cancellationToken);

		// Act
		await _loader.StartAsync(cancellationToken);

		// Assert
		Assert.Equal("CSoC", _registry.Find("CSoC")!.DisplayName);
	}
}
