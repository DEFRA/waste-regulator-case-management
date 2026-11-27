using System.Text.Json;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using NSubstitute;
using NUlid;
using WasteRegulatorCaseManagement.AuditEvents;
using WasteRegulatorCaseManagement.AuditEvents.Completion;

namespace WasteRegulatorCaseManagement.Test.AuditEvents.Completion;

public sealed class SnsCompletionEventSenderTests
{
	private const string TopicArn = "arn:aws:sns:eu-west-2:000000000000:waste_regulator_case_management_completion_events";

	private readonly IAmazonSimpleNotificationService _simpleNotificationService =
		Substitute.For<IAmazonSimpleNotificationService>();
	private readonly SnsCompletionEventSender _sender;
	private PublishRequest? _published;

	public SnsCompletionEventSenderTests()
	{
		_simpleNotificationService
			.PublishAsync(Arg.Do<PublishRequest>(request => _published = request), Arg.Any<CancellationToken>())
			.Returns(new PublishResponse());
		_sender = new SnsCompletionEventSender(
			_simpleNotificationService,
			Options.Create(new CaseCompletionEventProcessorOptions { TopicArn = TopicArn })
		);
	}

	[Fact]
	public async Task Send_publishes_the_envelope_as_json_with_routing_attributes()
	{
		var envelope = SampleEnvelope();

		await _sender.Send(envelope, TestContext.Current.CancellationToken);

		_published!.TopicArn.Should().Be(TopicArn);
		_published.MessageAttributes["Content-Type"].StringValue.Should().Be("application/json");
		_published.MessageAttributes["caseType"].StringValue.Should().Be(envelope.CaseType);
		_published.MessageAttributes["eventType"].StringValue.Should().Be(envelope.EventType);
		using var message = JsonDocument.Parse(_published.Message);
		message.RootElement.GetProperty("eventId").GetString().Should().Be(envelope.EventId);
		message.RootElement.GetProperty("caseId").GetString().Should().Be(envelope.CaseId);
		message.RootElement.GetProperty("payload").GetProperty("status").GetString().Should().Be("Accepted");
	}

	[Fact]
	public async Task Send_serializes_non_string_payload_values_in_completion_json()
	{
		var occurredAt = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
		var envelope = SampleEnvelope(occurredAt) with
		{
			Payload = new BsonDocument
			{
				{ "status", "Accepted" },
				{ "reason", BsonNull.Value },
				{ "when", new BsonDateTime(occurredAt) },
				{ "counts", new BsonArray { 1, 2 } },
				{ "active", true },
			},
		};

		await _sender.Send(envelope, TestContext.Current.CancellationToken);

		using var message = JsonDocument.Parse(_published!.Message);
		var payload = message.RootElement.GetProperty("payload");
		payload.GetProperty("reason").ValueKind.Should().Be(JsonValueKind.Null);
		payload.GetProperty("when").GetString().Should().Be("2026-11-26T12:00:00.000Z");
		payload.GetProperty("active").GetBoolean().Should().BeTrue();
		payload.GetProperty("counts")[0].GetInt32().Should().Be(1);
	}

	[Fact]
	public async Task Send_throws_when_the_json_message_exceeds_the_sns_size_limit()
	{
		var huge = new string('a', 256 * 1024);
		var envelope = SampleEnvelope() with { Payload = new BsonDocument("data", huge) };

		var send = () => _sender.Send(envelope, TestContext.Current.CancellationToken);

		await send.Should()
			.ThrowAsync<InvalidOperationException>()
			.WithMessage("Completion event message exceeds the SNS message size limit.");
		_published.Should().BeNull();
	}

	private static CaseCompletionEnvelope SampleEnvelope(
		DateTime? occurredAt = null
	)
	{
		var eventId = Ulid.NewUlid().ToString();
		var at = occurredAt ?? new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
		return new CaseCompletionEnvelope
		{
			EventId = eventId,
			CaseId = Ulid.NewUlid().ToString(),
			CaseType = "CSoC",
			SourceSystemId = Ulid.NewUlid().ToString(),
			OrgId = "org-1",
			StateId = "ACCEPTED",
			OccurredAt = at,
			EventType = "csoc.case.concluded",
			Payload = BsonDocument.Parse("""{"status":"Accepted"}"""),
		};
	}
}