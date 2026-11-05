using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using NSubstitute;
using NUlid;
using WasteRegulatorCaseManagement.AuditEvents.Analytics;

namespace WasteRegulatorCaseManagement.Test.AuditEvents.Analytics;

public sealed class SnsAnalyticsEventSenderTests
{
	private const string TopicArn = "arn:aws:sns:eu-west-2:000000000000:waste_regulator_case_management_analytics_events";
	private const int MaxMessageBodyBytes = (256 * 1024) - (4 * 1024);

	private readonly IAmazonSimpleNotificationService _simpleNotificationService =
		Substitute.For<IAmazonSimpleNotificationService>();
	private readonly SnsAnalyticsEventSender _sender;
	private PublishRequest? _published;

	public SnsAnalyticsEventSenderTests()
	{
		_simpleNotificationService
			.PublishAsync(Arg.Do<PublishRequest>(request => _published = request), Arg.Any<CancellationToken>())
			.Returns(new PublishResponse());
		_sender = new SnsAnalyticsEventSender(
			_simpleNotificationService,
			Options.Create(new AnalyticsAuditEventProcessorOptions { TopicArn = TopicArn })
		);
	}

	[Fact]
	public async Task Send_publishes_the_event_as_json_to_the_topic()
	{
		// Arrange
		var analyticsEvent = AnalyticsEventWithNote("Called the producer");

		// Act
		await _sender.Send(analyticsEvent, TestContext.Current.CancellationToken);

		// Assert
		Assert.Equal(TopicArn, _published!.TopicArn);
		Assert.Equal("application/json", _published.MessageAttributes["Content-Type"].StringValue);
		Assert.False(_published.MessageAttributes.ContainsKey("Content-Encoding"));
		using var message = JsonDocument.Parse(_published.Message);
		Assert.Equal("note-added", message.RootElement.GetProperty("eventType").GetString());
		Assert.Equal("2026-11-05T09:30:00.000Z", message.RootElement.GetProperty("occurredAt").GetString());
		Assert.Equal(
			"Called the producer",
			message.RootElement.GetProperty("after").GetProperty("note").GetString()
		);
	}

	[Fact]
	public async Task Send_compresses_an_event_too_large_for_one_message()
	{
		// Arrange
		var analyticsEvent = AnalyticsEventWithNote(new string('a', MaxMessageBodyBytes));

		// Act
		await _sender.Send(analyticsEvent, TestContext.Current.CancellationToken);

		// Assert
		Assert.Equal("gzip+base64", _published!.MessageAttributes["Content-Encoding"].StringValue);
		using var message = JsonDocument.Parse(Decompress(_published.Message));
		Assert.Equal(analyticsEvent.EventId, message.RootElement.GetProperty("eventId").GetString());
	}

	[Fact]
	public async Task Send_throws_when_the_compressed_event_is_still_too_large()
	{
		// Arrange
		var random = new Random(123);
		var incompressible = new string(
			Enumerable.Range(0, MaxMessageBodyBytes).Select(_ => (char)random.Next('A', 'z' + 1)).ToArray()
		);
		var analyticsEvent = AnalyticsEventWithNote(incompressible);

		// Act
		var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			_sender.Send(analyticsEvent, TestContext.Current.CancellationToken)
		);

		// Assert
		Assert.Equal("Analytics event message exceeds the SNS message size limit.", exception.Message);
		Assert.Null(_published);
	}

	private static AnalyticsEvent AnalyticsEventWithNote(string note) =>
		new()
		{
			EventId = Ulid.NewUlid().ToString(),
			Sequence = 7,
			Entity = "case",
			EntityId = $"case_{Ulid.NewUlid()}",
			Operation = "update",
			EventType = "note-added",
			OccurredAt = new DateTimeOffset(2026, 11, 5, 9, 30, 0, TimeSpan.Zero),
			RecordedAt = new DateTimeOffset(2026, 11, 5, 9, 30, 0, TimeSpan.Zero),
			Actor = "user:caseworker-1",
			Version = 2,
			Before = new BsonDocument("version", 1),
			After = new BsonDocument { { "version", 2 }, { "note", note } },
			SchemaVersion = "case.v1.0",
		};

	private static string Decompress(string message)
	{
		using var input = new MemoryStream(Convert.FromBase64String(message));
		using var gzip = new GZipStream(input, CompressionMode.Decompress);
		using var reader = new StreamReader(gzip, Encoding.UTF8);
		return reader.ReadToEnd();
	}
}
