using System.Text;
using System.Text.Json;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Options;

namespace WasteRegulatorCaseManagement.AuditEvents.Completion;

/// <summary>Publishes a case completion envelope to SNS as JSON.</summary>
public sealed class SnsCompletionEventSender(
	IAmazonSimpleNotificationService simpleNotificationService,
	IOptions<CaseCompletionEventProcessorOptions> options
) : ICompletionEventSender
{
	private const string JsonContentType = "application/json";
	private const string StringDataType = "String";

	private static readonly JsonSerializerOptions s_jsonSerializerOptions = new(JsonSerializerDefaults.Web);

	public async Task Send(CaseCompletionEnvelope envelope, CancellationToken cancellationToken)
	{
		var message = CaseCompletionSnsMessage.FromEnvelope(envelope);
		var serializedMessage = JsonSerializer.Serialize(message, s_jsonSerializerOptions);
		var request = new PublishRequest
		{
			TopicArn = options.Value.TopicArn,
			Message = serializedMessage,
			MessageAttributes = new Dictionary<string, MessageAttributeValue>
			{
				["Content-Type"] = new() { DataType = StringDataType, StringValue = JsonContentType },
				["caseType"] = new() { DataType = StringDataType, StringValue = envelope.CaseType },
				["eventType"] = new() { DataType = StringDataType, StringValue = envelope.EventType },
			},
		};

		if (Encoding.UTF8.GetByteCount(serializedMessage) > 256 * 1024)
		{
			throw new InvalidOperationException("Completion event message exceeds the SNS message size limit.");
		}

		await simpleNotificationService.PublishAsync(request, cancellationToken);
	}
}