using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WasteRegulatorCaseManagement.AuditEvents.Analytics;

public sealed class UtcMillisecondDateTimeOffsetJsonConverter : JsonConverter<DateTimeOffset>
{
	private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

	public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
		throw new NotSupportedException($"{nameof(UtcMillisecondDateTimeOffsetJsonConverter)} only supports writing.");

	public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
	{
		var utcValue = value.ToUniversalTime();
		var milliseconds = utcValue.Ticks - utcValue.Ticks % TimeSpan.TicksPerMillisecond;

		writer.WriteStringValue(new DateTimeOffset(milliseconds, TimeSpan.Zero).ToString(Format, CultureInfo.InvariantCulture));
	}
}
