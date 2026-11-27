using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using NUlid;
using WasteRegulatorCaseManagement.AuditEvents;
using WasteRegulatorCaseManagement.AuditEvents.Completion;
using WasteRegulatorCaseManagement.Test.TestSupport;

namespace WasteRegulatorCaseManagement.Test.AuditEvents.Completion;

public sealed class CaseCompletionAuditEventProcessorTests : IAsyncDisposable
{
	private const string Completion = AuditEventService.CompletionProcessName;
	private const int MaxDispatchAttempts = 5;

	private static readonly DateTime s_now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

	private readonly string _databaseName = MongoIntegrationFixture.NewDatabaseName("completion");
	private readonly TestMongoDbClientFactory _clientFactory;
	private readonly FakeTimeProvider _time = new(s_now);
	private readonly RecordingCompletionEventSender _sender = new();
	private readonly CaseCompletionAuditEventProcessor _processor;

	public CaseCompletionAuditEventProcessorTests(MongoIntegrationFixture fixture)
	{
		_clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
		_processor = new CaseCompletionAuditEventProcessor(
			new AuditEventLeaseService(_clientFactory, _time),
			new AuditEventDispatchService(_clientFactory, _time, NullLogger<AuditEventDispatchService>.Instance),
			_sender,
			Options.Create(
				new CaseCompletionEventProcessorOptions
				{
					ProcessingEnabled = true,
					TopicArn = "arn:aws:sns:eu-west-2:000000000000:waste_regulator_case_management_completion_events",
					MaxDispatchAttempts = MaxDispatchAttempts,
					FailedDispatchRetryDelaySeconds = 60,
				}
			),
			NullLogger<CaseCompletionAuditEventProcessor>.Instance
		);
	}

	private IMongoCollection<AuditEvent> AuditEvents =>
		_clientFactory.GetCollection<AuditEvent>(AuditEventService.AuditEventCollectionName);

	public async ValueTask DisposeAsync() =>
		await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);

	[Fact]
	public async Task ProcessBatch_skips_audit_events_without_a_completion_envelope()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await AuditEvents.InsertOneAsync(
			AuditEventCompletionTestFixtures.AnalyticsEvent(s_now, sequence: 1),
			cancellationToken: cancellationToken
		);

		await _processor.ProcessBatch(cancellationToken);

		_sender.SentEnvelopes.Should().BeEmpty();
	}

	[Fact]
	public async Task ProcessBatch_publishes_completion_envelopes_in_sequence_order_and_marks_them_dispatched()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await AuditEvents.InsertManyAsync(
			[
				AuditEventCompletionTestFixtures.CompletionEvent(s_now, sequence: 2),
				AuditEventCompletionTestFixtures.CompletionEvent(s_now, sequence: 1),
			],
			cancellationToken: cancellationToken
		);

		await _processor.ProcessBatch(cancellationToken);

		_sender.SentEnvelopes.Select(e => e.CaseId).Should().HaveCount(2);
		var stored = await AuditEvents.Find(FilterDefinition<AuditEvent>.Empty).ToListAsync(cancellationToken);
		stored
			.Select(auditEvent => auditEvent.Dispatches[Completion])
			.Should()
			.AllBeEquivalentTo(
				new AuditEventDispatch
				{
					Status = AuditEventDispatchStatus.Dispatched,
					Date = s_now,
					AttemptCount = 1,
				}
			);
	}

	[Fact]
	public async Task ProcessBatch_retries_a_failed_event_once_its_next_attempt_is_due()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var auditEvent = AuditEventCompletionTestFixtures.CompletionEvent(
			s_now,
			sequence: 1,
			dispatches: new()
			{
				[Completion] = new()
				{
					Status = AuditEventDispatchStatus.Failed,
					Date = s_now.AddMinutes(-2),
					Message = "Topic unavailable",
					AttemptCount = 1,
					NextAttemptAt = s_now.AddMinutes(-1),
				},
			}
		);
		await AuditEvents.InsertOneAsync(auditEvent, cancellationToken: cancellationToken);

		await _processor.ProcessBatch(cancellationToken);

		_sender.SentEnvelopes.Should().ContainSingle();
		var stored = await AuditEvents.Find(e => e.EventId == auditEvent.EventId).SingleAsync(cancellationToken);
		stored.Dispatches[Completion].Status.Should().Be(AuditEventDispatchStatus.Dispatched);
	}

	[Fact]
	public async Task ProcessBatch_marks_failed_when_a_publish_throws()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var auditEvent = AuditEventCompletionTestFixtures.CompletionEvent(s_now, sequence: 1);
		await AuditEvents.InsertOneAsync(auditEvent, cancellationToken: cancellationToken);
		_sender.FailFor(auditEvent.EventId, "Topic unavailable");

		await _processor.ProcessBatch(cancellationToken);

		_sender.SentEnvelopes.Should().ContainSingle();
		var stored = await AuditEvents.Find(e => e.EventId == auditEvent.EventId).SingleAsync(cancellationToken);
		stored.Dispatches[Completion].Should().BeEquivalentTo(
			new AuditEventDispatch
			{
				Status = AuditEventDispatchStatus.Failed,
				Date = s_now,
				Message = "Topic unavailable",
				AttemptCount = 1,
				NextAttemptAt = s_now.AddSeconds(60),
			}
		);
	}

	[Fact]
	public async Task ProcessBatch_publishes_nothing_while_another_instance_holds_the_lease()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		await AuditEvents.InsertOneAsync(
			AuditEventCompletionTestFixtures.CompletionEvent(s_now, sequence: 1),
			cancellationToken: cancellationToken
		);
		var otherInstance = new AuditEventLeaseService(_clientFactory, _time);
		await otherInstance.TryAcquire(Completion, TimeSpan.FromMinutes(1), cancellationToken);

		await _processor.ProcessBatch(cancellationToken);

		_sender.SentEnvelopes.Should().BeEmpty();
	}

	[Fact]
	public async Task ProcessBatch_stops_publishing_when_the_lease_is_lost_part_way_through()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var first = AuditEventCompletionTestFixtures.CompletionEvent(s_now, sequence: 1);
		var second = AuditEventCompletionTestFixtures.CompletionEvent(s_now, sequence: 2);
		await AuditEvents.InsertManyAsync([first, second], cancellationToken: cancellationToken);
		_sender.OnSend = () =>
			_clientFactory
				.GetCollection<AuditEventDispatchLease>(AuditEventService.AuditEventDispatchLeaseCollectionName)
				.UpdateOneAsync(
					lease => lease.Id == Completion,
					Builders<AuditEventDispatchLease>.Update.Set(lease => lease.Owner, "another-instance"),
					cancellationToken: cancellationToken
				);

		await _processor.ProcessBatch(cancellationToken);

		_sender.SentEnvelopes.Select(e => e.EventId).Should().Equal(first.EventId);
		var unsent = await AuditEvents.Find(e => e.EventId == second.EventId).SingleAsync(cancellationToken);
		unsent.Dispatches.Should().BeEmpty();
	}

	[Fact]
	public async Task ProcessBatch_dead_letters_an_event_whose_last_allowed_attempt_fails()
	{
		var cancellationToken = TestContext.Current.CancellationToken;
		var auditEvent = AuditEventCompletionTestFixtures.CompletionEvent(
			s_now,
			sequence: 1,
			dispatches: new()
			{
				[Completion] = new()
				{
					Status = AuditEventDispatchStatus.Failed,
					Date = s_now.AddMinutes(-2),
					Message = "Topic unavailable",
					AttemptCount = MaxDispatchAttempts - 1,
					NextAttemptAt = s_now.AddMinutes(-1),
				},
			}
		);
		await AuditEvents.InsertOneAsync(auditEvent, cancellationToken: cancellationToken);
		_sender.FailFor(auditEvent.EventId, "Topic unavailable");

		await _processor.ProcessBatch(cancellationToken);
		_time.Advance(TimeSpan.FromDays(1));
		await _processor.ProcessBatch(cancellationToken);

		var stored = await AuditEvents.Find(e => e.EventId == auditEvent.EventId).SingleAsync(cancellationToken);
		stored.Dispatches[Completion].Should().BeEquivalentTo(new AuditEventDispatch
		{
			Status = AuditEventDispatchStatus.DeadLettered,
			Date = s_now,
			Message = "Topic unavailable",
			AttemptCount = MaxDispatchAttempts,
		});
	}

	private sealed class RecordingCompletionEventSender : ICompletionEventSender
	{
		private readonly Dictionary<string, string> _failures = [];

		public List<CaseCompletionEnvelope> SentEnvelopes { get; } = [];

		public Func<Task>? OnSend { get; set; }

		public void FailFor(string eventId, string message) => _failures[eventId] = message;

		public async Task Send(CaseCompletionEnvelope envelope, CancellationToken cancellationToken)
		{
			SentEnvelopes.Add(envelope);

			if (_failures.TryGetValue(envelope.EventId, out var message))
			{
				throw new InvalidOperationException(message);
			}

			if (OnSend is not null)
			{
				await OnSend();
			}
		}
	}
}