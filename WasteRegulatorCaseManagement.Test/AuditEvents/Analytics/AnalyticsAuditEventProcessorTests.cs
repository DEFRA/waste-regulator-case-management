using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MongoDB.Bson;
using MongoDB.Driver;
using NUlid;
using WasteRegulatorCaseManagement.AuditEvents;
using WasteRegulatorCaseManagement.AuditEvents.Analytics;
using WasteRegulatorCaseManagement.Cases;
using WasteRegulatorCaseManagement.Test.Cases;
using WasteRegulatorCaseManagement.Test.TestSupport;

namespace WasteRegulatorCaseManagement.Test.AuditEvents.Analytics;

public sealed class AnalyticsAuditEventProcessorTests : IAsyncDisposable
{
	private const string Analytics = "analytics";
	private const int MaxDispatchAttempts = 5;

	private static readonly DateTime s_now = new(2026, 11, 5, 9, 30, 0, DateTimeKind.Utc);

	private readonly string _databaseName = MongoIntegrationFixture.NewDatabaseName("analytics");
	private readonly TestMongoDbClientFactory _clientFactory;
	private readonly FakeTimeProvider _time = new(s_now);
	private readonly RecordingAnalyticsEventSender _sender = new();
	private readonly AnalyticsAuditEventProcessor _processor;

	public AnalyticsAuditEventProcessorTests(MongoIntegrationFixture fixture)
	{
		_clientFactory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
		_processor = new AnalyticsAuditEventProcessor(
			new AuditEventLeaseService(_clientFactory, _time),
			new AuditEventDispatchService(_clientFactory, _time, NullLogger<AuditEventDispatchService>.Instance),
			_sender,
			Options.Create(
				new AnalyticsAuditEventProcessorOptions
				{
					ProcessingEnabled = true,
					TopicArn = "arn:aws:sns:eu-west-2:000000000000:waste_regulator_case_management_analytics_events",
					MaxDispatchAttempts = MaxDispatchAttempts,
					FailedDispatchRetryDelaySeconds = 60,
				}
			),
			NullLogger<AnalyticsAuditEventProcessor>.Instance
		);
	}

	private IMongoCollection<AuditEvent> AuditEvents =>
		_clientFactory.GetCollection<AuditEvent>(AuditEventService.AuditEventCollectionName);

	public async ValueTask DisposeAsync() =>
		await _clientFactory.GetClient().DropDatabaseAsync(_databaseName);

	[Fact]
	public async Task ProcessBatch_publishes_the_audit_event_recorded_with_a_new_case()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		var persistence = new CasePersistence(
			_clientFactory,
			NullLoggerFactory.Instance,
			new CaseRegistry([TestCaseType.Create("test", "Test type")]),
			new AuditEventService(_clientFactory, _time)
		);
		var caseRecord = new Case
		{
			SourceSystemId = Ulid.NewUlid().ToString(),
			CallbackUrl = "https://source-system.test/callback",
			Nation = "WALES",
			OrgId = "test-org",
			CaseType = "test",
			StateId = "submitted",
			SubmittedAt = s_now,
			LastModifiedAt = s_now,
			SubmittedBy = "service:case-intake",
			Timeline =
			{
				new CaseTimelineEntry
				{
					Action = "case-created",
					ActionDisplayName = "Case created",
					PerformedAt = s_now,
					PerformedById = "service:case-intake",
				},
			},
		};
		await persistence.CreateAsync(caseRecord, cancellationToken);

		// Act
		await _processor.ProcessBatch(cancellationToken);

		// Assert
		var sent = _sender.SentEvents.Should().ContainSingle().Subject;
		sent.Should()
			.BeEquivalentTo(
				new
				{
					Entity = "case",
					EntityId = $"case_{caseRecord.Id}",
					Operation = "create",
					EventType = "case-created",
					Actor = "service:case-intake",
					SchemaVersion = "case.v1.0",
					Before = (BsonDocument?)null,
				}
			);
		sent.After!["nation"].AsString.Should().Be("WALES");
		using var message = JsonDocument.Parse(JsonSerializer.Serialize(sent));
		message.RootElement.GetProperty("after").GetProperty("submittedAt").GetString().Should().Be("2026-11-05T09:30:00.000Z");
	}

	[Fact]
	public async Task ProcessBatch_publishes_unsent_events_in_sequence_order_and_marks_them_dispatched()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await AuditEvents.InsertManyAsync(
			[AuditEventWith(sequence: 2, dispatches: []), AuditEventWith(sequence: 1, dispatches: [])],
			cancellationToken: cancellationToken
		);

		// Act
		await _processor.ProcessBatch(cancellationToken);

		// Assert
		_sender.SentEvents.Select(e => e.Sequence).Should().Equal([1L, 2L]);
		var stored = await AuditEvents.Find(FilterDefinition<AuditEvent>.Empty).ToListAsync(cancellationToken);
		stored
			.Select(auditEvent => auditEvent.Dispatches[Analytics])
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
	public async Task ProcessBatch_does_not_publish_an_event_already_dispatched()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await AuditEvents.InsertOneAsync(
			AuditEventWith(
				sequence: 1,
				dispatches: new()
				{
					[Analytics] = new()
					{
						Status = AuditEventDispatchStatus.Dispatched,
						Date = s_now.AddMinutes(-5),
						AttemptCount = 1,
					},
				}
			),
			cancellationToken: cancellationToken
		);

		// Act
		await _processor.ProcessBatch(cancellationToken);

		// Assert
		_sender.SentEvents.Should().BeEmpty();
	}

	[Fact]
	public async Task ProcessBatch_retries_a_failed_event_once_its_next_attempt_is_due()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		var auditEvent = AuditEventWith(
			sequence: 1,
			dispatches: new()
			{
				[Analytics] = new()
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

		// Act
		await _processor.ProcessBatch(cancellationToken);

		// Assert
		_sender.SentEvents.Should().ContainSingle();
		var stored = await AuditEvents.Find(e => e.EventId == auditEvent.EventId).SingleAsync(cancellationToken);
		stored.Dispatches[Analytics].Status.Should().Be(AuditEventDispatchStatus.Dispatched);
		stored.Dispatches[Analytics].AttemptCount.Should().Be(2);
	}

	[Fact]
	public async Task ProcessBatch_does_not_retry_a_failed_event_before_its_next_attempt_is_due()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await AuditEvents.InsertOneAsync(
			AuditEventWith(
				sequence: 1,
				dispatches: new()
				{
					[Analytics] = new()
					{
						Status = AuditEventDispatchStatus.Failed,
						Date = s_now,
						Message = "Topic unavailable",
						AttemptCount = 1,
						NextAttemptAt = s_now.AddMinutes(1),
					},
				}
			),
			cancellationToken: cancellationToken
		);

		// Act
		await _processor.ProcessBatch(cancellationToken);

		// Assert
		_sender.SentEvents.Should().BeEmpty();
	}

	[Fact]
	public async Task ProcessBatch_marks_a_failed_publish_for_retry_and_publishes_the_remaining_events()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		var failing = AuditEventWith(sequence: 1, dispatches: []);
		var succeeding = AuditEventWith(sequence: 2, dispatches: []);
		await AuditEvents.InsertManyAsync([failing, succeeding], cancellationToken: cancellationToken);
		_sender.FailFor(failing.EventId, "Topic unavailable");

		// Act
		await _processor.ProcessBatch(cancellationToken);

		// Assert
		var failed = await AuditEvents.Find(e => e.EventId == failing.EventId).SingleAsync(cancellationToken);
		failed.Dispatches[Analytics].Should().BeEquivalentTo(new AuditEventDispatch
		{
			Status = AuditEventDispatchStatus.Failed,
			Date = s_now,
			Message = "Topic unavailable",
			AttemptCount = 1,
			NextAttemptAt = s_now.AddMinutes(1),
		});
		var dispatched = await AuditEvents.Find(e => e.EventId == succeeding.EventId).SingleAsync(cancellationToken);
		dispatched.Dispatches[Analytics].Status.Should().Be(AuditEventDispatchStatus.Dispatched);
	}

	[Fact]
	public async Task ProcessBatch_dead_letters_an_event_whose_last_allowed_attempt_fails()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		var auditEvent = AuditEventWith(
			sequence: 1,
			dispatches: new()
			{
				[Analytics] = new()
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

		// Act
		await _processor.ProcessBatch(cancellationToken);
		_time.Advance(TimeSpan.FromDays(1));
		await _processor.ProcessBatch(cancellationToken);

		// Assert
		_sender.SentEvents.Should().ContainSingle();
		var stored = await AuditEvents.Find(e => e.EventId == auditEvent.EventId).SingleAsync(cancellationToken);
		stored.Dispatches[Analytics].Should().BeEquivalentTo(new AuditEventDispatch
		{
			Status = AuditEventDispatchStatus.DeadLettered,
			Date = s_now,
			Message = "Topic unavailable",
			AttemptCount = MaxDispatchAttempts,
		});
	}

	[Fact]
	public async Task ProcessBatch_publishes_nothing_while_another_instance_holds_the_lease()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		await AuditEvents.InsertOneAsync(AuditEventWith(sequence: 1, dispatches: []), cancellationToken: cancellationToken);
		var otherInstance = new AuditEventLeaseService(_clientFactory, _time);
		await otherInstance.TryAcquire(Analytics, TimeSpan.FromMinutes(1), cancellationToken);

		// Act
		await _processor.ProcessBatch(cancellationToken);

		// Assert
		_sender.SentEvents.Should().BeEmpty();
	}

	[Fact]
	public async Task ProcessBatch_stops_publishing_when_the_lease_is_lost_part_way_through()
	{
		// Arrange
		var cancellationToken = TestContext.Current.CancellationToken;
		var first = AuditEventWith(sequence: 1, dispatches: []);
		var second = AuditEventWith(sequence: 2, dispatches: []);
		await AuditEvents.InsertManyAsync([first, second], cancellationToken: cancellationToken);
		_sender.OnSend = () =>
			_clientFactory
				.GetCollection<AuditEventDispatchLease>(AuditEventService.AuditEventDispatchLeaseCollectionName)
				.UpdateOneAsync(
					lease => lease.Id == Analytics,
					Builders<AuditEventDispatchLease>.Update.Set(lease => lease.Owner, "another-instance"),
					cancellationToken: cancellationToken
				);

		// Act
		await _processor.ProcessBatch(cancellationToken);

		// Assert
		_sender.SentEvents.Select(e => e.EventId).Should().Equal([first.EventId]);
		var unsent = await AuditEvents.Find(e => e.EventId == second.EventId).SingleAsync(cancellationToken);
		unsent.Dispatches.Should().BeEmpty();
	}

	private static AuditEvent AuditEventWith(long sequence, Dictionary<string, AuditEventDispatch> dispatches) =>
		new()
		{
			EventId = Ulid.NewUlid().ToString(),
			Sequence = sequence,
			Entity = "case",
			EntityId = $"case_{Ulid.NewUlid()}",
			Operation = "update",
			EventType = "note-added",
			OccurredAt = s_now.AddMinutes(-10),
			RecordedAt = s_now.AddMinutes(-10),
			Actor = "user:caseworker-1",
			Version = 2,
			Before = new BsonDocument("version", 1),
			After = new BsonDocument("version", 2),
			SchemaVersion = "case.v1.0",
			Dispatches = dispatches,
		};

	private sealed class RecordingAnalyticsEventSender : IAnalyticsEventSender
	{
		private readonly Dictionary<string, string> _failures = [];

		public List<AnalyticsEvent> SentEvents { get; } = [];

		public Func<Task>? OnSend { get; set; }

		public void FailFor(string eventId, string message) => _failures[eventId] = message;

		public async Task Send(AnalyticsEvent analyticsEvent, CancellationToken cancellationToken)
		{
			SentEvents.Add(analyticsEvent);

			if (_failures.TryGetValue(analyticsEvent.EventId, out var message))
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
