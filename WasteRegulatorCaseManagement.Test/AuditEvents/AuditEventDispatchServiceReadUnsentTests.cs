using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;
using WasteRegulatorCaseManagement.AuditEvents;
using WasteRegulatorCaseManagement.Test.TestSupport;

namespace WasteRegulatorCaseManagement.Test.AuditEvents;

public sealed class AuditEventDispatchServiceReadUnsentTests : IAsyncDisposable
{
	private const string Completion = AuditEventService.CompletionProcessName;

	private static readonly DateTime s_now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

	private readonly string _databaseName = MongoIntegrationFixture.NewDatabaseName("audit_read_unsent");
	private readonly TestMongoDbClientFactory _clientFactory;
	private readonly FakeTimeProvider _time = new(s_now);
	private readonly AuditEventDispatchService _sut;

	public AuditEventDispatchServiceReadUnsentTests(MongoIntegrationFixture fixture)
	{
		_clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
		_sut = new AuditEventDispatchService(_clientFactory, _time, NullLogger<AuditEventDispatchService>.Instance);
	}

	private IMongoCollection<AuditEvent> AuditEvents =>
		_clientFactory.GetCollection<AuditEvent>(AuditEventService.AuditEventCollectionName);

	public async ValueTask DisposeAsync() =>
		await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);

	[Fact]
	public async Task ReadUnsent_with_completion_filter_ignores_events_without_an_envelope()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await AuditEvents.InsertManyAsync(
			[
				AuditEventCompletionTestFixtures.AnalyticsEvent(s_now, sequence: 1),
				AuditEventCompletionTestFixtures.CompletionEvent(s_now, sequence: 2),
			],
			cancellationToken: cancellationToken
		);

		var batch = await _sut.ReadUnsent(
			Completion,
			batchSize: 10,
			cancellationToken,
			requireCompletionEnvelope: true
		);

		batch.Should().ContainSingle().Which.Sequence.Should().Be(2);
	}

	[Fact]
	public async Task ReadUnsent_without_completion_filter_includes_events_that_lack_an_envelope()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await AuditEvents.InsertOneAsync(
			AuditEventCompletionTestFixtures.AnalyticsEvent(s_now, sequence: 1),
			cancellationToken: cancellationToken
		);

		var batch = await _sut.ReadUnsent(
			"analytics",
			batchSize: 10,
			cancellationToken,
			requireCompletionEnvelope: false
		);

		batch.Should().ContainSingle().Which.Sequence.Should().Be(1);
	}
}