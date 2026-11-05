using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Microsoft.Extensions.Options;

namespace WasteRegulatorCaseManagement.AuditEvents.Analytics;

/// <summary>
/// Publishes an analytics event to the topic as JSON, gzip-compressed and
/// base64-encoded when it would not otherwise fit in an SNS message.
/// </summary>
public sealed class SnsAnalyticsEventSender(
	IAmazonSimpleNotificationService simpleNotificationService,
	IOptions<AnalyticsAuditEventProcessorOptions> options
) : IAnalyticsEventSender
{
	private const int SnsMessageLimitBytes = 256 * 1024;
	private const int MessageSizeBufferBytes = 4 * 1024;
	private const int MaxMessageBodyBytes = SnsMessageLimitBytes - MessageSizeBufferBytes;
	private const string JsonContentType = "application/json";
	private const string StringDataType = "String";

	private static readonly JsonSerializerOptions s_jsonSerializerOptions = new(JsonSerializerDefaults.Web);

	public async Task Send(AnalyticsEvent analyticsEvent, CancellationToken cancellationToken)
	{
		var serializedMessage = JsonSerializer.Serialize(analyticsEvent, s_jsonSerializerOptions);
		var request = new PublishRequest
		{
			TopicArn = options.Value.TopicArn,
			MessageAttributes = new Dictionary<string, MessageAttributeValue>
			{
				["Content-Type"] = new() { DataType = StringDataType, StringValue = JsonContentType },
			},
		};

		if (Encoding.UTF8.GetByteCount(serializedMessage) <= MaxMessageBodyBytes)
		{
			request.Message = serializedMessage;
		}
		else
		{
			request.Message = Compress(serializedMessage);
			if (Encoding.UTF8.GetByteCount(request.Message) > MaxMessageBodyBytes)
			{
				throw new InvalidOperationException("Analytics event message exceeds the SNS message size limit.");
			}

			request.MessageAttributes["Content-Encoding"] = new()
			{
				DataType = StringDataType,
				StringValue = "gzip+base64",
			};
		}

		await simpleNotificationService.PublishAsync(request, cancellationToken);
	}

	private static string Compress(string serializedMessage)
	{
		using var output = new MemoryStream();
		using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
		{
			gzip.Write(Encoding.UTF8.GetBytes(serializedMessage));
		}

		return Convert.ToBase64String(output.ToArray());
	}
}
