using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Driver;
using WasteRegulatorCaseManagement.AuditEvents;
using WasteRegulatorCaseManagement.Test.TestSupport;

namespace WasteRegulatorCaseManagement.Test.AuditEvents;

public sealed class AuditEventDispatchServiceMarkTests : IAsyncDisposable
{
	private const string Completion = AuditEventService.CompletionProcessName;

	private static readonly DateTime s_now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

	private readonly string _databaseName = MongoIntegrationFixture.NewDatabaseName("audit_mark");
	private readonly TestMongoDbClientFactory _clientFactory;
	private readonly FakeTimeProvider _time = new(s_now);
	private readonly ListLogger<AuditEventDispatchService> _logger = new();
	private readonly AuditEventDispatchService _sut;

	public AuditEventDispatchServiceMarkTests(MongoIntegrationFixture fixture)
	{
		_clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
		_sut = new AuditEventDispatchService(_clientFactory, _time, _logger);
	}

	private IMongoCollection<AuditEvent> AuditEvents =>
		_clientFactory.GetCollection<AuditEvent>(AuditEventService.AuditEventCollectionName);

	public async ValueTask DisposeAsync() =>
		await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);

	[Fact]
	public async Task MarkDispatched_logs_error_when_event_was_already_dispatched_concurrently()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var auditEvent = AuditEventCompletionTestFixtures.CompletionEvent(
			s_now,
			sequence: 1,
			dispatches: new()
			{
				[Completion] = new()
				{
					Status = AuditEventDispatchStatus.Dispatched,
					Date = s_now,
					AttemptCount = 1,
				},
			}
		);
		await AuditEvents.InsertOneAsync(auditEvent, cancellationToken: cancellationToken);

		// Mark on an already-dispatched event: the filter won't match (status is Dispatched,
		// not Failed, and the dispatch path already exists), so ModifiedCount == 0.
		await _sut.MarkDispatched(Completion, auditEvent, cancellationToken);

		_logger.Records.Should().ContainSingle(r => r.Level == LogLevel.Error)
			.Which.Message.Should().Contain(auditEvent.EventId);
	}

	[Fact]
	public async Task MarkFailed_logs_error_when_event_cannot_be_updated()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var auditEvent = AuditEventCompletionTestFixtures.CompletionEvent(s_now, sequence: 1);
		// Do NOT insert the event into the database — MarkFailed on a missing event gets ModifiedCount == 0.

		await _sut.MarkFailed(
			Completion,
			auditEvent,
			new InvalidOperationException("Topic unavailable"),
			maxDispatchAttempts: 5,
			failedDispatchRetryDelay: TimeSpan.FromSeconds(60),
			cancellationToken
		);

		_logger.Records.Should().ContainSingle(r => r.Level == LogLevel.Error)
			.Which.Message.Should().Contain(auditEvent.EventId);
	}
}
